using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Repair
{
    public enum ScheduleRepairScenarioKind
    {
        Swap,
        Create,
        Move,
        Drag,
        Update
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

    public interface IScheduleRepairAdapter
    {
        Task<ScheduleRepairPlan> BuildPlanAsync(ScheduleRepairRequest request, CancellationToken cancellationToken = default);
    }

    public sealed class ScheduleRepairAdapter : IScheduleRepairAdapter
    {
        private const long ProposedTaskId = -1;

        private readonly ScheduleDbContext _db;
        private readonly IScheduleTimeService _timeService;
        private readonly IRouteChainBuilder _routeChainBuilder;
        private readonly IDayScheduleFitCore _fitCore;

        public ScheduleRepairAdapter(
            ScheduleDbContext db,
            IScheduleTimeService timeService,
            IRouteChainBuilder routeChainBuilder,
            IDayScheduleFitCore fitCore)
        {
            _db = db;
            _timeService = timeService;
            _routeChainBuilder = routeChainBuilder;
            _fitCore = fitCore;
        }

        public async Task<ScheduleRepairPlan> BuildPlanAsync(ScheduleRepairRequest request, CancellationToken cancellationToken = default)
        {
            return request.ScenarioKind switch
            {
                ScheduleRepairScenarioKind.Swap => await BuildSwapPlanAsync(request, cancellationToken),
                _ => await BuildPlacementPlanAsync(request, cancellationToken)
            };
        }

        private async Task<ScheduleRepairPlan> BuildSwapPlanAsync(ScheduleRepairRequest request, CancellationToken cancellationToken)
        {
            if (!request.SourceTaskId.HasValue || !request.TargetTaskId.HasValue)
            {
                return ScheduleRepairPlan.Fail("Nepakanka duomenu smart swap planui.");
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
                return ScheduleRepairPlan.Fail("Uzduotis nerasta.");
            }

            if (source.TechnicianId != target.TechnicianId || source.StartAt.Date != target.StartAt.Date)
            {
                return ScheduleRepairPlan.Fail("Smart swap galimas tik to paties techniko dienoje.");
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

            var chainTaskIds = chain?.Stops
                .Where(s => s.TaskId.HasValue)
                .Select(s => s.TaskId!.Value)
                .Distinct()
                .ToList() ?? new List<long>();

            var tasks = await LoadTaskSnapshotsAsync(chainTaskIds, cancellationToken);
            if (!tasks.Any(t => t.TaskId == source.Id) || !tasks.Any(t => t.TaskId == target.Id))
            {
                return ScheduleRepairPlan.Fail("Nepavyko rasti dienos grandines smart swap veiksmui.");
            }

            var workplaceAddressId = await ResolveWorkplaceAddressIdAsync(source.TechnicianId, cancellationToken);
            if (workplaceAddressId <= 0)
            {
                return ScheduleRepairPlan.Fail("Techniko darbo vietos adresas nerastas.");
            }

            var fitRequest = await BuildFitRequestAsync(
                tasks,
                source.StartAt.Date,
                request.TimezoneOffsetMinutes,
                workplaceAddressId,
                new[]
                {
                    new ScheduleRepairAnchor(source.Id, target.StartAt),
                    new ScheduleRepairAnchor(target.Id, source.StartAt)
                },
                cancellationToken);

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
            CancellationToken cancellationToken)
        {
            var routeLookup = await BuildRouteLookupAsync(tasks, workplaceAddressId, cancellationToken);
            var localDay = utcDay.AddMinutes(timezoneOffsetMinutes).Date;
            var dayStartUtc = _timeService.CreateUtcFromDateAndMinutes(localDay, Services.Scheduling.ScheduleGrid.DayStartMinutes, timezoneOffsetMinutes);
            var dayEndUtc = _timeService.CreateUtcFromDateAndMinutes(localDay, Services.Scheduling.ScheduleGrid.DayEndMinutes, timezoneOffsetMinutes);

            return new DayScheduleFitRequest(
                tasks,
                anchors,
                routeLookup ?? new Dictionary<(long From, long To), int>(),
                workplaceAddressId,
                dayStartUtc,
                dayEndUtc,
                timezoneOffsetMinutes);
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
    }
}
