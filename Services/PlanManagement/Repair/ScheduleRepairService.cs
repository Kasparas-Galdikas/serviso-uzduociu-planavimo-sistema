using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Repair
{
    public enum ScheduleRepairScenarioKind
    {
        Swap,
        SwapAdjust,
        Create,
        Move,
        Drag,
        Update,
        Compact,
        Delete
    }

    public sealed record ScheduleRepairPlacement(
        long TaskId,
        long ServiceAddressId,
        DateTime StartAt,
        DateTime EndAt);

    public sealed class ScheduleRepairRequest
    {
        public ScheduleRepairScenarioKind ScenarioKind { get; init; }
        public int TimezoneOffsetMinutes { get; init; }
        public long TechnicianId { get; init; }
        public DateTime DayUtc { get; init; }
        public long? PlanId { get; init; }
        public bool FilterByPlanId { get; init; }
        public long? SourceTaskId { get; init; }
        public long? TargetTaskId { get; init; }
        public long? ExcludeTaskId { get; init; }
        public ScheduleRepairPlacement? ProposedPlacement { get; init; }
        public IReadOnlySet<long>? FixedTaskIds { get; init; }
    }

    public sealed record ScheduleRepairPlan(
        bool Success,
        string? Message,
        ScheduleRepairKind RepairKind,
        IReadOnlyList<DayScheduleFitAdjustment> Adjustments,
        ScheduleRepairPlacement? PrimaryPlacement)
    {
        public static ScheduleRepairPlan Fail(string message) =>
            new(false, message, ScheduleRepairKind.NoFit, Array.Empty<DayScheduleFitAdjustment>(), null);
    }

    public sealed record ScheduleRepairApplyResult(
        bool Success,
        string? Message,
        IReadOnlyList<long> AppliedTaskIds)
    {
        public static ScheduleRepairApplyResult Fail(string message) =>
            new(false, message, Array.Empty<long>());
    }

    public interface IScheduleRepairService
    {
        Task<ScheduleRepairPlan> BuildPlanAsync(ScheduleRepairRequest request, CancellationToken cancellationToken = default);
        Task<ScheduleRepairApplyResult> ApplyPlanAsync(
            ScheduleRepairPlan plan,
            long? primaryTaskId,
            CancellationToken cancellationToken = default);
    }

    public sealed class ScheduleRepairService : IScheduleRepairService
    {
        private const long ProposedTaskId = -1;

        private readonly ScheduleDbContext _db;
        private readonly IScheduleTimeService _timeService;
        private readonly IRouteChainBuilder _routeChainBuilder;
        private readonly IDayScheduleFitCore _fitCore;
        private readonly ITravelTimeResolver _travelResolver;
        private readonly IRouteEvaluationService _routeEvalService;

        public ScheduleRepairService(
            ScheduleDbContext db,
            IScheduleTimeService timeService,
            IRouteChainBuilder routeChainBuilder,
            IDayScheduleFitCore fitCore,
            ITravelTimeResolver travelResolver,
            IRouteEvaluationService routeEvalService)
        {
            _db = db;
            _timeService = timeService;
            _routeChainBuilder = routeChainBuilder;
            _fitCore = fitCore;
            _travelResolver = travelResolver;
            _routeEvalService = routeEvalService;
        }

        public async Task<ScheduleRepairPlan> BuildPlanAsync(ScheduleRepairRequest request, CancellationToken cancellationToken = default)
        {
            return request.ScenarioKind switch
            {
                ScheduleRepairScenarioKind.Swap => await BuildSwapPlanAsync(request, cancellationToken),
                ScheduleRepairScenarioKind.SwapAdjust => await BuildSwapAdjustPlanAsync(request, cancellationToken),
                ScheduleRepairScenarioKind.Compact => await BuildCompactPlanAsync(request, cancellationToken),
                ScheduleRepairScenarioKind.Delete => await BuildCompactPlanAsync(request, cancellationToken),
                _ => await BuildPlacementPlanAsync(request, cancellationToken)
            };
        }

        public async Task<ScheduleRepairApplyResult> ApplyPlanAsync(
            ScheduleRepairPlan plan,
            long? primaryTaskId,
            CancellationToken cancellationToken = default)
        {
            if (!plan.Success)
            {
                return ScheduleRepairApplyResult.Fail(plan.Message ?? "Nepavyko pritaikyti taisymo plano.");
            }

            var appliedIds = new List<long>();

            if (plan.PrimaryPlacement != null && primaryTaskId.HasValue)
            {
                var primaryTask = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == primaryTaskId.Value, cancellationToken);
                if (primaryTask == null)
                {
                    return ScheduleRepairApplyResult.Fail("Nepavyko rasti pagrindines uzduoties taisymo pritaikymui.");
                }

                primaryTask.StartAt = plan.PrimaryPlacement.StartAt;
                primaryTask.EndAt = plan.PrimaryPlacement.EndAt;
                appliedIds.Add(primaryTask.Id);
            }

            var realAdjustments = plan.Adjustments
                .Where(x => x.TaskId > 0 && (!primaryTaskId.HasValue || x.TaskId != primaryTaskId.Value))
                .ToList();

            if (realAdjustments.Count > 0)
            {
                var adjustmentIds = realAdjustments.Select(x => x.TaskId).Distinct().ToList();
                var tasksById = await _db.Tasks
                    .Where(t => adjustmentIds.Contains(t.Id))
                    .ToDictionaryAsync(t => t.Id, cancellationToken);

                if (tasksById.Count != adjustmentIds.Count)
                {
                    return ScheduleRepairApplyResult.Fail("Nepavyko rasti visu uzduociu taisymo pritaikymui.");
                }

                foreach (var adjustment in realAdjustments)
                {
                    var task = tasksById[adjustment.TaskId];
                    task.StartAt = adjustment.StartAt;
                    task.EndAt = adjustment.EndAt;
                    appliedIds.Add(task.Id);
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            return new ScheduleRepairApplyResult(true, null, appliedIds.Distinct().ToList());
        }

        private async Task<ScheduleRepairPlan> BuildSwapPlanAsync(ScheduleRepairRequest request, CancellationToken cancellationToken)
        {
            var context = await BuildSwapContextAsync(request, reorderSegment: true, cancellationToken);
            if (!context.Success)
            {
                return ScheduleRepairPlan.Fail(context.Message!);
            }

            var fixedStarts = new Dictionary<long, DateTime>
            {
                [context.Source.TaskId] = context.Target.StartAt,
                [context.Target.TaskId] = context.Source.StartAt
            };

            return BuildSwapFitPlan(context, fixedStarts);
        }

        private async Task<ScheduleRepairPlan> BuildSwapAdjustPlanAsync(ScheduleRepairRequest request, CancellationToken cancellationToken)
        {
            var context = await BuildSwapContextAsync(request, reorderSegment: false, cancellationToken);
            if (!context.Success)
            {
                return ScheduleRepairPlan.Fail(context.Message!);
            }

            var fixedStarts = new Dictionary<long, DateTime>
            {
                [context.Source.TaskId] = context.Source.StartAt,
                [context.Target.TaskId] = context.Target.StartAt
            };

            return BuildSwapFitPlan(context, fixedStarts);
        }

        private ScheduleRepairPlan BuildSwapFitPlan(SwapContext context, IReadOnlyDictionary<long, DateTime> fixedStarts)
        {
            var tasks = context.OrderedTasks;
            var n = tasks.Count;
            var currentUtc = _timeService.GetCurrentUtcTime();
            var effectiveDayStart = context.StartIndex == 0 && currentUtc > context.DayStartUtc
                ? currentUtc
                : context.DayStartUtc;

            // Backward pass: compute the latest each task can start so all subsequent
            // tasks still fit within DayEndUtc (slot-floor aligned so results are valid slots).
            var latestStarts = new DateTime[n];
            latestStarts[n - 1] = AlignUtcToSlotFloor(context.DayEndUtc - tasks[n - 1].Duration, context.TimezoneOffsetMinutes);
            for (var i = n - 2; i >= 0; i--)
            {
                var travel = ResolveTravelMinutes(context.RouteLookup, tasks[i].AddressId, tasks[i + 1].AddressId);
                if (!travel.HasValue)
                    return ScheduleRepairPlan.Fail("Nepavyko ivertinti keliones (truksta marsruto duomenu).");
                latestStarts[i] = AlignUtcToSlotFloor(
                    latestStarts[i + 1].AddMinutes(-travel.Value) - tasks[i].Duration,
                    context.TimezoneOffsetMinutes);
            }

            // Forward pass: place each task at clamp(preferred, actualEarliest, latestStart).
            // Rolling prevEnd/prevAddr track the actual end of the previously placed task.
            var scheduledStarts = new Dictionary<long, DateTime>();
            var prevEnd = effectiveDayStart;
            var prevAddressId = context.WorkplaceAddressId;

            for (var i = 0; i < n; i++)
            {
                var task = tasks[i];

                var travel = ResolveTravelMinutes(context.RouteLookup, prevAddressId, task.AddressId);
                if (!travel.HasValue)
                    return ScheduleRepairPlan.Fail("Nepavyko ivertinti keliones (truksta marsruto duomenu).");

                var actualEarliest = AlignUtcToSlotCeiling(prevEnd.AddMinutes(travel.Value), context.TimezoneOffsetMinutes);

                if (actualEarliest > latestStarts[i])
                    return ScheduleRepairPlan.Fail("Nepakanka laisvo laiko sukeitimo koregavimui.");

                var preferred = fixedStarts.TryGetValue(task.TaskId, out var fixedStart)
                    ? fixedStart
                    : task.StartAt;

                // Clamp preferred to [actualEarliest, latestStart]: slide forward if travel
                // requires it, pull back if placing at preferred would overflow the day end.
                // Pre-segment tasks use task.StartAt as preferred so they only move when the
                // backward chain requires it and there is a gap to absorb the shift.
                var scheduledStart = preferred < actualEarliest
                    ? actualEarliest
                    : preferred > latestStarts[i]
                        ? latestStarts[i]
                        : preferred;

                scheduledStarts[task.TaskId] = scheduledStart;
                prevEnd = scheduledStart + task.Duration;
                prevAddressId = task.AddressId;
            }

            var adjustments = context.OrderedTasks
                .Where(t => scheduledStarts[t.TaskId] != t.StartAt)
                .Select(t => new DayScheduleFitAdjustment(
                    t.TaskId,
                    scheduledStarts[t.TaskId],
                    scheduledStarts[t.TaskId] + t.Duration))
                .ToList();

            return new ScheduleRepairPlan(true, null, ScheduleRepairKind.Swapped, adjustments, null);
        }

        private async Task<SwapContext> BuildSwapContextAsync(ScheduleRepairRequest request, bool reorderSegment, CancellationToken cancellationToken)
        {
            if (!request.SourceTaskId.HasValue || !request.TargetTaskId.HasValue)
            {
                return SwapContext.Fail("Nepakanka duomenu smart swap planui.");
            }

            var source = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == request.SourceTaskId.Value, cancellationToken);
            var target = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == request.TargetTaskId.Value, cancellationToken);

            if (source == null || target == null)
            {
                return SwapContext.Fail("Uzduotis nerasta.");
            }

            if (source.TechnicianId != target.TechnicianId || source.StartAt.Date != target.StartAt.Date)
            {
                return SwapContext.Fail("Smart swap galimas tik to paties techniko dienoje.");
            }

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

            var orderedTaskIds = chain?.Stops
                .Where(s => s.TaskId.HasValue)
                .Select(s => s.TaskId!.Value)
                .Distinct()
                .ToList() ?? new List<long>();

            var orderedTasks = await LoadTaskSnapshotsAsync(orderedTaskIds, cancellationToken);
            var sourceIndex = orderedTasks.FindIndex(t => t.TaskId == source.Id);
            var targetIndex = orderedTasks.FindIndex(t => t.TaskId == target.Id);
            if (sourceIndex < 0 || targetIndex < 0)
            {
                return SwapContext.Fail("Nepavyko rasti dienos grandines smart swap veiksmui.");
            }

            var startIndex = Math.Min(sourceIndex, targetIndex);
            var endIndex = Math.Max(sourceIndex, targetIndex);
            IReadOnlyList<ScheduleRepairTaskSnapshot> effectiveOrder;
            if (reorderSegment)
            {
                var reordered = new List<ScheduleRepairTaskSnapshot>();
                reordered.AddRange(orderedTasks.Take(startIndex));
                reordered.Add(orderedTasks[endIndex]);
                if (endIndex - startIndex > 1)
                {
                    reordered.AddRange(orderedTasks.Skip(startIndex + 1).Take(endIndex - startIndex - 1));
                }
                reordered.Add(orderedTasks[startIndex]);
                if (orderedTasks.Count - endIndex - 1 > 0)
                {
                    reordered.AddRange(orderedTasks.Skip(endIndex + 1));
                }
                effectiveOrder = reordered;
            }
            else
            {
                effectiveOrder = orderedTasks;
            }

            var workplaceAddressId = await ResolveWorkplaceAddressIdAsync(source.TechnicianId, cancellationToken);
            if (workplaceAddressId <= 0)
            {
                return SwapContext.Fail("Techniko darbo vietos adresas nerastas.");
            }

            var routeLookup = await BuildRouteLookupAsync(effectiveOrder, workplaceAddressId, cancellationToken);
            if (routeLookup == null || routeLookup.Count == 0)
            {
                return SwapContext.Fail("Nepavyko ivertinti keliones (truksta marsruto duomenu).");
            }

            var localDay = source.StartAt.Date.AddMinutes(request.TimezoneOffsetMinutes).Date;
            var dayStartUtc = _timeService.CreateUtcFromDateAndMinutes(localDay, ScheduleGrid.DayStartMinutes, request.TimezoneOffsetMinutes);
            var dayEndUtc = _timeService.CreateUtcFromDateAndMinutes(localDay, ScheduleGrid.DayEndMinutes, request.TimezoneOffsetMinutes);
            var effectiveSourceIndex = effectiveOrder.ToList().FindIndex(t => t.TaskId == source.Id);
            var effectiveTargetIndex = effectiveOrder.ToList().FindIndex(t => t.TaskId == target.Id);

            return new SwapContext(
                true,
                null,
                effectiveOrder,
                effectiveOrder.First(t => t.TaskId == source.Id),
                effectiveOrder.First(t => t.TaskId == target.Id),
                workplaceAddressId,
                routeLookup,
                dayStartUtc,
                dayEndUtc,
                Math.Min(effectiveSourceIndex, effectiveTargetIndex),
                Math.Max(effectiveSourceIndex, effectiveTargetIndex),
                request.TimezoneOffsetMinutes);
        }

        private async Task<ScheduleRepairPlan> BuildCompactPlanAsync(ScheduleRepairRequest request, CancellationToken cancellationToken)
        {
            var tasks = await LoadDayTaskSnapshotsAsync(
                request.TechnicianId,
                request.DayUtc,
                request.FilterByPlanId ? request.PlanId : null,
                null,
                cancellationToken);

            if (tasks.Count <= 1)
            {
                return ScheduleRepairPlan.Fail("Nepakanka uzduociu suglaudinimui.");
            }

            var workplaceAddressId = await ResolveWorkplaceAddressIdAsync(request.TechnicianId, cancellationToken);
            if (workplaceAddressId <= 0)
            {
                return ScheduleRepairPlan.Fail("Techniko darbo vietos adresas nerastas.");
            }

            var fixedIds = request.FixedTaskIds ?? new HashSet<long>();
            var localDay = request.DayUtc.AddMinutes(request.TimezoneOffsetMinutes).Date;
            var dayStartUtc = _timeService.CreateUtcFromDateAndMinutes(localDay, ScheduleGrid.DayStartMinutes, request.TimezoneOffsetMinutes);

            var anchors = tasks
                .Select(t => new ScheduleRepairAnchor(
                    t.TaskId,
                    fixedIds.Contains(t.TaskId) ? t.StartAt : dayStartUtc))
                .ToList();

            var fitRequest = await BuildFitRequestAsync(
                tasks,
                request.DayUtc.Date,
                request.TimezoneOffsetMinutes,
                workplaceAddressId,
                anchors,
                cancellationToken,
                immovableTaskIds: fixedIds.Count > 0 ? fixedIds : null);

            var fitResult = await _fitCore.FitAsync(fitRequest, cancellationToken);
            return MapFitResult(fitResult, null);
        }

        private async Task<ScheduleRepairPlan> BuildPlacementPlanAsync(ScheduleRepairRequest request, CancellationToken cancellationToken)
        {
            if (request.ProposedPlacement == null)
            {
                return ScheduleRepairPlan.Fail("Nepakanka duomenu vietos taisymo planui.");
            }

            var tasks = await LoadDayTaskSnapshotsAsync(
                request.TechnicianId,
                request.DayUtc,
                request.FilterByPlanId ? request.PlanId : null,
                request.ExcludeTaskId,
                cancellationToken);

            tasks.Add(new ScheduleRepairTaskSnapshot(
                ProposedTaskId,
                request.ProposedPlacement.ServiceAddressId,
                request.ProposedPlacement.StartAt,
                request.ProposedPlacement.EndAt - request.ProposedPlacement.StartAt));

            var workplaceAddressId = await ResolveWorkplaceAddressIdAsync(request.TechnicianId, cancellationToken);
            if (workplaceAddressId <= 0)
            {
                return ScheduleRepairPlan.Fail("Techniko darbo vietos adresas nerastas.");
            }

            var fitRequest = await BuildFitRequestAsync(
                tasks,
                request.DayUtc.Date,
                request.TimezoneOffsetMinutes,
                workplaceAddressId,
                new[] { new ScheduleRepairAnchor(ProposedTaskId, request.ProposedPlacement.StartAt) },
                cancellationToken);

            var fitResult = await _fitCore.FitAsync(fitRequest, cancellationToken);
            return MapFitResult(fitResult, ProposedTaskId, request.ProposedPlacement.ServiceAddressId);
        }

        private async Task<DayScheduleFitRequest> BuildFitRequestAsync(
            IReadOnlyList<ScheduleRepairTaskSnapshot> tasks,
            DateTime utcDay,
            int timezoneOffsetMinutes,
            long workplaceAddressId,
            IReadOnlyList<ScheduleRepairAnchor> anchors,
            CancellationToken cancellationToken,
            IReadOnlySet<long>? immovableTaskIds = null)
        {
            var routeLookup = await BuildRouteLookupAsync(tasks, workplaceAddressId, cancellationToken);
            var localDay = utcDay.AddMinutes(timezoneOffsetMinutes).Date;
            var dayStartUtc = _timeService.CreateUtcFromDateAndMinutes(localDay, ScheduleGrid.DayStartMinutes, timezoneOffsetMinutes);
            var dayEndUtc = _timeService.CreateUtcFromDateAndMinutes(localDay, ScheduleGrid.DayEndMinutes, timezoneOffsetMinutes);

            return new DayScheduleFitRequest(
                tasks,
                anchors,
                routeLookup ?? new Dictionary<(long From, long To), int>(),
                workplaceAddressId,
                dayStartUtc,
                dayEndUtc,
                timezoneOffsetMinutes,
                NowUtc: _timeService.GetCurrentUtcTime(),
                ImmovableTaskIds: immovableTaskIds);
        }

        private static ScheduleRepairPlan MapFitResult(DayScheduleFitResult fitResult, long? primaryTaskId, long? primaryAddressId = null)
        {
            if (!fitResult.Success)
            {
                return ScheduleRepairPlan.Fail(fitResult.Message ?? "Nepavyko sudaryti taisymo plano.");
            }

            ScheduleRepairPlacement? primaryPlacement = null;
            if (primaryTaskId.HasValue && primaryAddressId.HasValue)
            {
                var adjustment = fitResult.Adjustments.FirstOrDefault(x => x.TaskId == primaryTaskId.Value);
                if (adjustment != null)
                {
                    primaryPlacement = new ScheduleRepairPlacement(
                        primaryTaskId.Value,
                        primaryAddressId.Value,
                        adjustment.StartAt,
                        adjustment.EndAt);
                }
            }

            return new ScheduleRepairPlan(
                true,
                null,
                fitResult.RepairKind,
                fitResult.Adjustments,
                primaryPlacement);
        }

        private async Task<long> ResolveWorkplaceAddressIdAsync(long technicianId, CancellationToken cancellationToken)
        {
            return await _db.Technicians
                .AsNoTracking()
                .Where(t => t.Id == technicianId)
                .Select(t => t.WorkplaceAddressId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        private async Task<List<ScheduleRepairTaskSnapshot>> LoadTaskSnapshotsAsync(IReadOnlyCollection<long> taskIds, CancellationToken cancellationToken)
        {
            var tasks = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.ServiceObject)
                .Where(t => taskIds.Contains(t.Id))
                .ToListAsync(cancellationToken);

            return taskIds
                .Join(tasks, id => id, task => task.Id, (_, task) => new ScheduleRepairTaskSnapshot(
                    task.Id,
                    task.ServiceObject.AddressId,
                    task.StartAt,
                    task.EndAt - task.StartAt))
                .ToList();
        }

        private async Task<List<ScheduleRepairTaskSnapshot>> LoadDayTaskSnapshotsAsync(
            long technicianId,
            DateTime utcDay,
            long? planId,
            long? excludeTaskId,
            CancellationToken cancellationToken)
        {
            var dayStart = utcDay.Date;
            var dayEnd = dayStart.AddDays(1);
            var query = _db.Tasks
                .AsNoTracking()
                .Include(t => t.ServiceObject)
                .Where(t => t.TechnicianId == technicianId)
                .Where(t => t.StartAt >= dayStart && t.StartAt < dayEnd)
                .Where(t => t.Status != ScheduleTaskStatus.Canceled);

            if (excludeTaskId.HasValue)
            {
                query = query.Where(t => t.Id != excludeTaskId.Value);
            }

            if (planId.HasValue)
            {
                query = query.Where(t => t.PlanId == planId.Value);
            }

            return await query
                .OrderBy(t => t.StartAt)
                .Select(t => new ScheduleRepairTaskSnapshot(
                    t.Id,
                    t.ServiceObject.AddressId,
                    t.StartAt,
                    t.EndAt - t.StartAt))
                .ToListAsync(cancellationToken);
        }

        private async Task<Dictionary<(long From, long To), int>?> BuildRouteLookupAsync(
            IReadOnlyCollection<ScheduleRepairTaskSnapshot> tasks,
            long workplaceAddressId,
            CancellationToken cancellationToken)
        {
            var addressIds = tasks
                .Select(t => t.AddressId)
                .Append(workplaceAddressId)
                .Distinct()
                .ToList();

            await _routeEvalService.EnsureAllPairwiseRoutesAsync(addressIds, cancellationToken);
            return await _travelResolver.GetTravelMinutesAsync(addressIds, cancellationToken);
        }

        private static int? ResolveTravelMinutes(IReadOnlyDictionary<(long From, long To), int> routeLookup, long fromAddressId, long toAddressId)
        {
            if (fromAddressId == toAddressId)
            {
                return 0;
            }

            return routeLookup.TryGetValue((fromAddressId, toAddressId), out var minutes) ? minutes : null;
        }

        private static DateTime AlignUtcToSlotFloor(DateTime utc, int timezoneOffsetMinutes)
        {
            var local = utc.AddMinutes(timezoneOffsetMinutes);
            var floored = new DateTime(local.Year, local.Month, local.Day, local.Hour,
                (local.Minute / ScheduleGrid.SlotSizeMinutes) * ScheduleGrid.SlotSizeMinutes,
                0, local.Kind);
            return DateTime.SpecifyKind(floored.AddMinutes(-timezoneOffsetMinutes), DateTimeKind.Utc);
        }

        private static DateTime AlignUtcToSlotCeiling(DateTime utc, int timezoneOffsetMinutes)
        {
            var local = utc.AddMinutes(timezoneOffsetMinutes);
            var rounded = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, local.Kind);
            var remainder = rounded.Minute % ScheduleGrid.SlotSizeMinutes;
            if (rounded.Second > 0 || rounded.Millisecond > 0 || remainder != 0)
            {
                var delta = ScheduleGrid.SlotSizeMinutes - remainder;
                if (remainder == 0 && (local.Second > 0 || local.Millisecond > 0))
                {
                    delta = ScheduleGrid.SlotSizeMinutes;
                }

                rounded = rounded.AddMinutes(delta);
            }

            return DateTime.SpecifyKind(rounded.AddMinutes(-timezoneOffsetMinutes), DateTimeKind.Utc);
        }

        private sealed record SwapContext(
            bool Success,
            string? Message,
            IReadOnlyList<ScheduleRepairTaskSnapshot> OrderedTasks,
            ScheduleRepairTaskSnapshot Source,
            ScheduleRepairTaskSnapshot Target,
            long WorkplaceAddressId,
            IReadOnlyDictionary<(long From, long To), int> RouteLookup,
            DateTime DayStartUtc,
            DateTime DayEndUtc,
            int StartIndex,
            int EndIndex,
            int TimezoneOffsetMinutes)
        {
            private static readonly ScheduleRepairTaskSnapshot EmptyTask = new(0, 0, default, TimeSpan.Zero);

            public static SwapContext Fail(string message) =>
                new(false, message, Array.Empty<ScheduleRepairTaskSnapshot>(), EmptyTask, EmptyTask, 0, new Dictionary<(long From, long To), int>(), default, default, 0, 0, 0);
        }
    }
}
