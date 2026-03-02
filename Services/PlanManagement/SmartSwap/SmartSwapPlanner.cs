using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.SmartSwap
{
    public sealed class SmartSwapPlanner : ISmartSwapPlanner
    {
        private readonly ScheduleDbContext _db;
        private readonly IScheduleTimeService _timeService;
        private readonly IRouteChainBuilder _routeChainBuilder;
        private readonly ILogger<SmartSwapPlanner> _logger;

        public SmartSwapPlanner(
            ScheduleDbContext db,
            IScheduleTimeService timeService,
            IRouteChainBuilder routeChainBuilder,
            ILogger<SmartSwapPlanner> logger)
        {
            _db = db;
            _timeService = timeService;
            _routeChainBuilder = routeChainBuilder;
            _logger = logger;
        }

        public async Task<SmartSwapPlan> BuildPlanAsync(
            long sourceTaskId,
            long targetTaskId,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
        {
            if (sourceTaskId == targetTaskId)
            {
                return SmartSwapPlan.Fail("Negalima sukeisti uzduoties su pacia savimi.");
            }

            var source = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == sourceTaskId, cancellationToken);
            var target = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == targetTaskId, cancellationToken);

            if (source == null || target == null)
            {
                return SmartSwapPlan.Fail("Uzduotis nerasta.");
            }

            if (source.TechnicianId != target.TechnicianId || source.StartAt.Date != target.StartAt.Date)
            {
                return SmartSwapPlan.Fail("Smart swap galimas tik to paties techniko dienoje.");
            }

            var localDay = source.StartAt.AddMinutes(timezoneOffsetMinutes).Date;
            var dayStartUtc = _timeService.CreateUtcFromDateAndMinutes(localDay, ScheduleGrid.DayStartMinutes, timezoneOffsetMinutes);
            var dayEndUtc = _timeService.CreateUtcFromDateAndMinutes(localDay, ScheduleGrid.DayEndMinutes, timezoneOffsetMinutes);

            _logger.LogInformation(
                "swap-smart.planner.window sourceTaskId={SourceTaskId} targetTaskId={TargetTaskId} localDay={LocalDay:yyyy-MM-dd} dayStartUtc={DayStartUtc:o} dayEndUtc={DayEndUtc:o} sourceStartUtc={SourceStartUtc:o} targetStartUtc={TargetStartUtc:o}",
                sourceTaskId,
                targetTaskId,
                localDay,
                dayStartUtc,
                dayEndUtc,
                source.StartAt,
                target.StartAt);

            var chain = await _routeChainBuilder.BuildDayChainAsync(
                new RouteChainRequest
                {
                    TechnicianId = source.TechnicianId,
                    DayUtc = source.StartAt.Date,
                    IncludeCanceled = false,
                    FilterByPlanId = true,
                    PlanId = source.PlanId
                },
                cancellationToken);

            var chainTaskIds = chain?.Stops
                .Where(s => s.TaskId.HasValue)
                .Select(s => s.TaskId!.Value)
                .Distinct()
                .ToList() ?? new List<long>();

            var chainTasks = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.ServiceObject)
                .Where(t => chainTaskIds.Contains(t.Id))
                .ToListAsync(cancellationToken);
            var chainTaskLookup = chainTasks.ToDictionary(t => t.Id, t => t);
            var dayTasks = chainTaskIds
                .Where(id => chainTaskLookup.ContainsKey(id))
                .Select(id => chainTaskLookup[id])
                .ToList();

            _logger.LogInformation(
                "swap-smart.planner.day-chain source=route-chain-builder technicianId={TechnicianId} utcDay={UtcDay:yyyy-MM-dd} localDay={LocalDay:yyyy-MM-dd} dayTaskIds=[{TaskIds}]",
                source.TechnicianId,
                source.StartAt.Date,
                localDay,
                string.Join(", ", dayTasks.Select(t => $"{t.Id}@{t.StartAt:HH:mm}")));

            if (!dayTasks.Any(t => t.Id == sourceTaskId) || !dayTasks.Any(t => t.Id == targetTaskId))
            {
                _logger.LogWarning(
                    "swap-smart.planner.day-chain-miss sourceTaskId={SourceTaskId} targetTaskId={TargetTaskId} hasSource={HasSource} hasTarget={HasTarget}",
                    sourceTaskId,
                    targetTaskId,
                    dayTasks.Any(t => t.Id == sourceTaskId),
                    dayTasks.Any(t => t.Id == targetTaskId));
                return SmartSwapPlan.Fail("Nepavyko rasti dienos grandines smart swap veiksmui.");
            }

            var workplaceAddressId = await _db.Technicians
                .AsNoTracking()
                .Where(t => t.Id == source.TechnicianId)
                .Select(t => t.WorkplaceAddressId)
                .FirstOrDefaultAsync(cancellationToken);
            if (workplaceAddressId <= 0)
            {
                return SmartSwapPlan.Fail("Techniko darbo vietos adresas nerastas.");
            }

            var addressLabels = await BuildAddressLabelLookupAsync(dayTasks, workplaceAddressId, cancellationToken);

            var routeLookup = await BuildRouteLookupAsync(dayTasks, workplaceAddressId, cancellationToken);
            if (routeLookup == null)
            {
                return SmartSwapPlan.Fail("Nepavyko ivertinti keliones (truksta marsruto duomenu).");
            }

            var starts = dayTasks.ToDictionary(t => t.Id, t => t.StartAt);
            var originalStarts = dayTasks.ToDictionary(t => t.Id, t => t.StartAt);
            var durations = dayTasks.ToDictionary(t => t.Id, t => t.EndAt - t.StartAt);
            var byId = dayTasks.ToDictionary(t => t.Id, t => t);

            // Swap anchor starts first, then repair with minimal forward pushes.
            starts[sourceTaskId] = target.StartAt;
            starts[targetTaskId] = source.StartAt;

            var maxIterations = Math.Max(8, dayTasks.Count * 6);
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

                    DateTime minStartUtc;
                    if (i == 0)
                    {
                        var required = TryResolveRouteMinutes(routeLookup, workplaceAddressId, task.ServiceObject.AddressId);
                        if (!required.HasValue)
                        {
                            var toAddressId = task.ServiceObject.AddressId;
                            _logger.LogWarning(
                                "swap-smart.planner.missing-route fromAddressId={FromAddressId} fromAddress={FromAddress} toAddressId={ToAddressId} toAddress={ToAddress} taskId={TaskId} segment=workplace-first",
                                workplaceAddressId,
                                ResolveAddressLabel(addressLabels, workplaceAddressId),
                                toAddressId,
                                ResolveAddressLabel(addressLabels, toAddressId),
                                task.Id);
                            return SmartSwapPlan.Fail("Nepavyko ivertinti keliones (truksta marsruto duomenu).");
                        }
                        minStartUtc = dayStartUtc.AddMinutes(required.Value);
                    }
                    else
                    {
                        var prevTaskId = ordered[i - 1];
                        var prevTask = byId[prevTaskId];
                        var prevEnd = starts[prevTaskId] + durations[prevTaskId];
                        var fromAddressId = prevTask.ServiceObject.AddressId;
                        var toAddressId = task.ServiceObject.AddressId;
                        var required = TryResolveRouteMinutes(routeLookup, fromAddressId, toAddressId);
                        if (!required.HasValue)
                        {
                            _logger.LogWarning(
                                "swap-smart.planner.missing-route fromAddressId={FromAddressId} fromAddress={FromAddress} toAddressId={ToAddressId} toAddress={ToAddress} prevTaskId={PrevTaskId} taskId={TaskId} segment=task-task",
                                fromAddressId,
                                ResolveAddressLabel(addressLabels, fromAddressId),
                                toAddressId,
                                ResolveAddressLabel(addressLabels, toAddressId),
                                prevTask.Id,
                                task.Id);
                            return SmartSwapPlan.Fail("Nepavyko ivertinti keliones (truksta marsruto duomenu).");
                        }
                        minStartUtc = prevEnd.AddMinutes(required.Value);
                    }

                    if (currentStart < minStartUtc)
                    {
                        var delta = (int)Math.Ceiling((minStartUtc - currentStart).TotalMinutes);
                        delta = AlignToSlot(delta);
                        if (delta <= 0) continue;
                        ShiftSuffix(ordered, starts, i, delta);
                        changed = true;
                    }

                    currentStart = starts[taskId];
                    currentEnd = currentStart + durations[taskId];
                    if (currentEnd > dayEndUtc)
                    {
                        return SmartSwapPlan.Fail("Nepakanka laisvo laiko dienos pabaigoje smart swap taisymui.");
                    }
                }

                if (!changed)
                {
                    var adjustments = starts
                        .Where(kvp => kvp.Value != originalStarts[kvp.Key])
                        .OrderBy(kvp => kvp.Value)
                        .Select(kvp => new SmartSwapAdjustment(
                            kvp.Key,
                            kvp.Value,
                            kvp.Value + durations[kvp.Key]))
                        .ToList();

                    if (adjustments.Count == 0)
                    {
                        return SmartSwapPlan.Fail("Smart swap nerado reikalingu pakeitimu.");
                    }

                    return new SmartSwapPlan(true, null, adjustments);
                }
            }

            return SmartSwapPlan.Fail("Smart swap virsijo taisymo bandymu limita.");
        }

        private static int AlignToSlot(int minutes)
        {
            var slot = ScheduleGrid.SlotSizeMinutes;
            if (minutes <= 0) return 0;
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

        private static int? TryResolveRouteMinutes(
            IReadOnlyDictionary<(long From, long To), int> routeLookup,
            long fromAddressId,
            long toAddressId)
        {
            if (fromAddressId == toAddressId) return 0;
            if (!routeLookup.TryGetValue((fromAddressId, toAddressId), out var minutes) || minutes <= 0)
            {
                return null;
            }
            return minutes;
        }

        private async Task<Dictionary<(long From, long To), int>?> BuildRouteLookupAsync(
            IReadOnlyCollection<TaskEntity> dayTasks,
            long workplaceAddressId,
            CancellationToken cancellationToken)
        {
            var addressIds = dayTasks
                .Select(t => t.ServiceObject.AddressId)
                .Append(workplaceAddressId)
                .Distinct()
                .ToList();

            var routes = await _db.TravelRoutes
                .AsNoTracking()
                .Where(r => addressIds.Contains(r.FromAddressId) && addressIds.Contains(r.ToAddressId))
                .ToListAsync(cancellationToken);

            return routes
                .GroupBy(r => (r.FromAddressId, r.ToAddressId))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(x => x.DurationMinutes).First().DurationMinutes);
        }

        private async Task<Dictionary<long, string>> BuildAddressLabelLookupAsync(
            IReadOnlyCollection<TaskEntity> dayTasks,
            long workplaceAddressId,
            CancellationToken cancellationToken)
        {
            var addressIds = dayTasks
                .Select(t => t.ServiceObject.AddressId)
                .Append(workplaceAddressId)
                .Distinct()
                .ToList();

            return await _db.Addresses
                .AsNoTracking()
                .Where(a => addressIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.FullAddress, cancellationToken);
        }

        private static string ResolveAddressLabel(IReadOnlyDictionary<long, string> addressLabels, long addressId)
        {
            return addressLabels.TryGetValue(addressId, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : "unknown-address";
        }
    }
}
