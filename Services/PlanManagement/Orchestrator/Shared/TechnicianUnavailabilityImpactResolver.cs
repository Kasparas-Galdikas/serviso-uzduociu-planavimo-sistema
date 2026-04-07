using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.Unavailability;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared
{
    internal sealed class TechnicianUnavailabilityImpactResolver
    {
        private readonly ScheduleDbContext _db;
        private readonly ITravelTimeResolver _travelResolver;
        private readonly IScheduleTimeService _timeService;
        private readonly TechnicianUnavailabilityReader _technicianAvailabilityPolicy;

        public TechnicianUnavailabilityImpactResolver(
            ScheduleDbContext db,
            ITravelTimeResolver travelResolver,
            IScheduleTimeService timeService,
            TechnicianUnavailabilityReader technicianAvailabilityPolicy)
        {
            _db = db;
            _travelResolver = travelResolver;
            _timeService = timeService;
            _technicianAvailabilityPolicy = technicianAvailabilityPolicy;
        }

        internal sealed record AffectedTaskCandidate(TaskEntity Task, DateTime EffectiveStartAt);

        public async Task<List<AffectedTaskCandidate>> GetAffectedTasksAsync(
            long technicianId,
            DateTime periodStart,
            DateTime periodEnd,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken)
        {
            var dayStartUtc = periodStart.Date;
            var dayEndUtcExclusive = periodEnd.Date.AddDays(1);

            var tasks = await _db.Tasks
                .Include(t => t.Plan)
                .Include(t => t.ServiceObject)
                    .ThenInclude(o => o.Address)
                .AsNoTracking()
                .Where(t => t.TechnicianId == technicianId
                         && t.Status == ScheduleTaskStatus.InProgress
                         && t.StartAt >= dayStartUtc
                         && t.StartAt < dayEndUtcExclusive)
                .OrderBy(t => t.StartAt)
                .ToListAsync(cancellationToken);

            if (tasks.Count == 0)
            {
                return new List<AffectedTaskCandidate>();
            }

            var workplaceAddressId = await _db.Technicians
                .AsNoTracking()
                .Where(t => t.Id == technicianId)
                .Select(t => (long?)t.WorkplaceAddressId)
                .FirstOrDefaultAsync(cancellationToken);

            var addressIds = tasks
                .Select(task => task.ServiceObject?.AddressId)
                .Where(id => id.HasValue && id.Value > 0)
                .Select(id => id!.Value)
                .ToHashSet();

            if (workplaceAddressId.HasValue && workplaceAddressId.Value > 0)
            {
                addressIds.Add(workplaceAddressId.Value);
            }

            var routeLookup = addressIds.Count == 0
                ? new Dictionary<(long From, long To), int>()
                : await _travelResolver.GetTravelMinutesAsync(addressIds, cancellationToken);

            var affected = new List<AffectedTaskCandidate>();
            foreach (var dayGroup in tasks.GroupBy(t => t.StartAt.Date).OrderBy(g => g.Key))
            {
                TaskEntity? previous = null;
                foreach (var task in dayGroup.OrderBy(t => t.StartAt))
                {
                    var effectiveStartAt = ResolveEffectiveStartAt(
                        task,
                        previous,
                        workplaceAddressId,
                        routeLookup,
                        timezoneOffsetMinutes,
                        _timeService);
                    if (_technicianAvailabilityPolicy.HasBlockedOverlap(
                        new[] { new TechnicianBlockedInterval(periodStart, periodEnd) },
                        effectiveStartAt,
                        task.EndAt))
                    {
                        affected.Add(new AffectedTaskCandidate(task, effectiveStartAt));
                    }

                    previous = task;
                }
            }

            return affected;
        }

        private static DateTime ResolveEffectiveStartAt(
            TaskEntity task,
            TaskEntity? previous,
            long? workplaceAddressId,
            IReadOnlyDictionary<(long From, long To), int> routeLookup,
            int timezoneOffsetMinutes,
            IScheduleTimeService timeService)
        {
            var earliestAllowedStart = previous?.EndAt ?? GetLocalDayStartUtc(task.StartAt, timezoneOffsetMinutes, timeService);
            var fromAddressId = previous?.ServiceObject?.AddressId ?? workplaceAddressId;
            var toAddressId = task.ServiceObject?.AddressId;

            if (!fromAddressId.HasValue || !toAddressId.HasValue)
            {
                return task.StartAt;
            }

            if (!routeLookup.TryGetValue((fromAddressId.Value, toAddressId.Value), out var requiredTravelMinutes) ||
                requiredTravelMinutes <= 0)
            {
                return task.StartAt;
            }

            var availableGapMinutes = (int)(task.StartAt - earliestAllowedStart).TotalMinutes;
            if (availableGapMinutes <= 0)
            {
                return task.StartAt;
            }

            var travelMinutes = Math.Min(requiredTravelMinutes, availableGapMinutes);
            var effectiveStartAt = task.StartAt.AddMinutes(-travelMinutes);
            return effectiveStartAt < earliestAllowedStart ? earliestAllowedStart : effectiveStartAt;
        }

        private static DateTime GetLocalDayStartUtc(
            DateTime taskStartAtUtc,
            int timezoneOffsetMinutes,
            IScheduleTimeService timeService)
        {
            var localTaskDate = timeService.ConvertUtcToLocal(taskStartAtUtc, timezoneOffsetMinutes).Date;
            return timeService.CreateUtcFromDateAndMinutes(localTaskDate, ScheduleGrid.DayStartMinutes, timezoneOffsetMinutes);
        }
    }
}
