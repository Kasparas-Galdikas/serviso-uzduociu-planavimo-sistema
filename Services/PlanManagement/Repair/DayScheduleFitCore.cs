using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Services.Scheduling;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Repair
{
    public enum ScheduleRepairKind
    {
        None,
        ExactFit,
        Shifted,
        Compacted,
        Swapped,
        NoFit
    }

    public sealed record ScheduleRepairTaskSnapshot(
        long TaskId,
        long AddressId,
        DateTime StartAt,
        TimeSpan Duration);

    public sealed record ScheduleRepairAnchor(
        long TaskId,
        DateTime DesiredStartAt);

    public sealed record DayScheduleFitRequest(
        IReadOnlyList<ScheduleRepairTaskSnapshot> Tasks,
        IReadOnlyList<ScheduleRepairAnchor> Anchors,
        IReadOnlyDictionary<(long From, long To), int> RouteLookup,
        long WorkplaceAddressId,
        DateTime DayStartUtc,
        DateTime DayEndUtc,
        int TimezoneOffsetMinutes,
        DateTime NowUtc = default,
        IReadOnlySet<long>? ImmovableTaskIds = null,
        IReadOnlyList<DayScheduleBlockedInterval>? BlockedIntervals = null,
        bool AllowBlockedIntervalShift = false);

    public sealed record DayScheduleBlockedInterval(
        DateTime StartAt,
        DateTime EndAt);

    public sealed record DayScheduleFitAdjustment(
        long TaskId,
        DateTime StartAt,
        DateTime EndAt);

    public sealed record DayScheduleFitResult(
        bool Success,
        string? Message,
        ScheduleRepairKind RepairKind,
        IReadOnlyList<DayScheduleFitAdjustment> Adjustments)
    {
        public static DayScheduleFitResult Fail(string message) =>
            new(false, message, ScheduleRepairKind.NoFit, Array.Empty<DayScheduleFitAdjustment>());
    }

    public interface IDayScheduleFitCore
    {
        Task<DayScheduleFitResult> FitAsync(DayScheduleFitRequest request, CancellationToken cancellationToken = default);
    }

    public sealed class DayScheduleFitCore : IDayScheduleFitCore
    {
        public Task<DayScheduleFitResult> FitAsync(DayScheduleFitRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Tasks.Count == 0)
            {
                return Task.FromResult(DayScheduleFitResult.Fail("Nera dienos uzduociu planavimo taisymui."));
            }

            if (request.RouteLookup.Count == 0)
            {
                return Task.FromResult(DayScheduleFitResult.Fail("Nepavyko ivertinti keliones (truksta marsruto duomenu)."));
            }

            var byId = request.Tasks.ToDictionary(t => t.TaskId);
            var starts = request.Tasks.ToDictionary(t => t.TaskId, t => t.StartAt);
            var originalStarts = request.Tasks.ToDictionary(t => t.TaskId, t => t.StartAt);
            var durations = request.Tasks.ToDictionary(t => t.TaskId, t => t.Duration);

            foreach (var anchor in request.Anchors)
            {
                if (starts.ContainsKey(anchor.TaskId))
                {
                    starts[anchor.TaskId] = anchor.DesiredStartAt;
                }
            }

            var maxIterations = Math.Max(8, request.Tasks.Count * 6);
            var chainPulledBack = false;

            for (var iter = 0; iter < maxIterations; iter++)
            {
                var changed = false;
                var ordered = starts
                    .OrderBy(x => x.Value)
                    .Select(x => x.Key)
                    .ToList();

                for (var i = 0; i < ordered.Count; i++)
                {
                    var taskId = ordered[i];
                    var task = byId[taskId];
                    var currentStart = starts[taskId];
                    var currentEnd = currentStart + durations[taskId];
                    var occupancyFloorUtc = i == 0
                        ? (request.NowUtc > request.DayStartUtc ? request.NowUtc : request.DayStartUtc)
                        : starts[ordered[i - 1]] + durations[ordered[i - 1]];
                    var requiredTravelMinutes = 0;

                    DateTime minStartUtc;
                    if (i == 0)
                    {
                        var required = TryResolveRouteMinutes(request.RouteLookup, request.WorkplaceAddressId, task.AddressId);
                        if (!required.HasValue)
                        {
                            return Task.FromResult(DayScheduleFitResult.Fail("Nepavyko ivertinti keliones (truksta marsruto duomenu)."));
                        }

                        requiredTravelMinutes = required.Value;
                        var effectiveDayStart = request.NowUtc > request.DayStartUtc ? request.NowUtc : request.DayStartUtc;
                        minStartUtc = effectiveDayStart.AddMinutes(requiredTravelMinutes);
                    }
                    else
                    {
                        var prevTaskId = ordered[i - 1];
                        var prevTask = byId[prevTaskId];
                        var prevEnd = starts[prevTaskId] + durations[prevTaskId];
                        var required = TryResolveRouteMinutes(request.RouteLookup, prevTask.AddressId, task.AddressId);
                        if (!required.HasValue)
                        {
                            return Task.FromResult(DayScheduleFitResult.Fail("Nepavyko ivertinti keliones (truksta marsruto duomenu)."));
                        }

                        requiredTravelMinutes = required.Value;
                        minStartUtc = prevEnd.AddMinutes(requiredTravelMinutes);
                    }

                    minStartUtc = AlignUtcToSlotCeiling(minStartUtc, request.TimezoneOffsetMinutes);

                    if (currentStart < minStartUtc)
                    {
                        if (request.ImmovableTaskIds != null && request.ImmovableTaskIds.Contains(taskId))
                        {
                            return Task.FromResult(DayScheduleFitResult.Fail("Fiksuota uzduotis negali buti perkelta (truksta laiko kelionei)."));
                        }

                        var delta = (int)Math.Ceiling((minStartUtc - currentStart).TotalMinutes);
                        delta = AlignToSlot(delta);
                        if (delta > 0)
                        {
                            ShiftSuffix(ordered, starts, i, delta);
                            changed = true;
                        }
                    }

                    currentStart = starts[taskId];
                    currentEnd = currentStart + durations[taskId];
                    var effectiveOccupiedStart = currentStart.AddMinutes(-requiredTravelMinutes);
                    if (effectiveOccupiedStart < occupancyFloorUtc)
                    {
                        effectiveOccupiedStart = occupancyFloorUtc;
                    }

                    var overlappingBlockedInterval = request.BlockedIntervals?
                        .Where(interval => interval.StartAt < currentEnd && interval.EndAt > effectiveOccupiedStart)
                        .OrderBy(interval => interval.EndAt)
                        .FirstOrDefault();

                    if (overlappingBlockedInterval != null)
                    {
                        if (!request.AllowBlockedIntervalShift)
                        {
                            return Task.FromResult(DayScheduleFitResult.Fail("Pasirinktas laikas pažymėtas kaip techniko neaktyvumas."));
                        }

                        var blockedFreeStart = AlignUtcToSlotCeiling(
                            overlappingBlockedInterval.EndAt,
                            request.TimezoneOffsetMinutes);
                        var delta = (int)Math.Ceiling((blockedFreeStart - currentStart).TotalMinutes);
                        delta = AlignToSlot(delta);
                        if (delta > 0)
                        {
                            ShiftSuffix(ordered, starts, i, delta);
                            changed = true;
                            continue;
                        }
                    }

                    if (currentEnd <= request.DayEndUtc)
                    {
                        continue;
                    }

                    if (currentStart > minStartUtc)
                    {
                        var minutesFromDayStart = (int)Math.Ceiling((minStartUtc - request.DayStartUtc).TotalMinutes);
                        var alignedStart = request.DayStartUtc.AddMinutes(AlignToSlot(minutesFromDayStart));
                        if (alignedStart + durations[taskId] <= request.DayEndUtc)
                        {
                            starts[taskId] = alignedStart;
                            changed = true;
                            continue;
                        }
                    }

                    if (!chainPulledBack)
                    {
                        var firstId = ordered[0];
                        var firstTask = byId[firstId];
                        var travelFromWorkplace = TryResolveRouteMinutes(request.RouteLookup, request.WorkplaceAddressId, firstTask.AddressId) ?? 0;
                        var earliestStart = request.DayStartUtc.AddMinutes(AlignToSlot(travelFromWorkplace));
                        var pullBackMinutes = (int)(starts[firstId] - earliestStart).TotalMinutes;
                        if (pullBackMinutes > 0)
                        {
                            foreach (var id in ordered)
                            {
                                starts[id] = starts[id].AddMinutes(-pullBackMinutes);
                            }

                            chainPulledBack = true;
                            changed = true;
                            break;
                        }
                    }

                    return Task.FromResult(DayScheduleFitResult.Fail("Nepakanka laisvo laiko dienos pabaigoje planavimo taisymui."));
                }

                if (changed)
                {
                    continue;
                }

                var adjustments = starts
                    .Where(kvp => kvp.Value != originalStarts[kvp.Key])
                    .OrderBy(kvp => kvp.Value)
                    .Select(kvp => new DayScheduleFitAdjustment(
                        kvp.Key,
                        kvp.Value,
                        kvp.Value + durations[kvp.Key]))
                    .ToList();

                var repairKind = ResolveRepairKind(request.Anchors, adjustments);
                return Task.FromResult(new DayScheduleFitResult(true, null, repairKind, adjustments));
            }

            return Task.FromResult(DayScheduleFitResult.Fail("Planavimo taisymas virsijo bandymu limita."));
        }

        private static ScheduleRepairKind ResolveRepairKind(
            IReadOnlyList<ScheduleRepairAnchor> anchors,
            IReadOnlyList<DayScheduleFitAdjustment> adjustments)
        {
            if (adjustments.Count == 0)
            {
                return ScheduleRepairKind.ExactFit;
            }

            if (anchors.Count >= 2)
            {
                return ScheduleRepairKind.Swapped;
            }

            return adjustments.Count == 1
                ? ScheduleRepairKind.Shifted
                : ScheduleRepairKind.Compacted;
        }

        /// <summary>
        /// Helper: Resolve travel minutes from the RouteLookup dictionary.
        /// Same logic as removed TryResolveRouteMinutes but kept local for compatibility with signature.
        /// </summary>
        private static int? TryResolveRouteMinutes(
            IReadOnlyDictionary<(long From, long To), int> routeLookup,
            long fromAddressId,
            long toAddressId)
        {
            if (fromAddressId == toAddressId)
            {
                return 0;
            }

            return routeLookup.TryGetValue((fromAddressId, toAddressId), out var minutes) && minutes >= 0
                ? minutes
                : null;
        }

        private static int AlignToSlot(int minutes)
        {
            var slot = ScheduleGrid.SlotSizeMinutes;
            if (minutes <= 0)
            {
                return 0;
            }

            return ((minutes + slot - 1) / slot) * slot;
        }

        private static void ShiftSuffix(
            IReadOnlyList<long> ordered,
            IDictionary<long, DateTime> starts,
            int startIndex,
            int deltaMinutes)
        {
            for (var i = startIndex; i < ordered.Count; i++)
            {
                var id = ordered[i];
                starts[id] = starts[id].AddMinutes(deltaMinutes);
            }
        }

        private static DateTime AlignUtcToSlotCeiling(DateTime utc, int timezoneOffsetMinutes)
        {
            var local = utc.AddMinutes(timezoneOffsetMinutes);
            var localMinutes = (int)local.TimeOfDay.TotalMinutes;
            var alignedMinutes = AlignToSlot(localMinutes);
            if (alignedMinutes == localMinutes)
            {
                return utc;
            }

            var alignedLocal = local.Date.AddMinutes(alignedMinutes);
            return alignedLocal.AddMinutes(-timezoneOffsetMinutes);
        }
    }
}
