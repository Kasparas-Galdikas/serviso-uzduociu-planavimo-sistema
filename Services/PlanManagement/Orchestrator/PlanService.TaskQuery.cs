using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Data.Seed;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator
{
    internal class PlanServiceTaskQuery
    {
        private readonly ScheduleDbContext _db;
        private readonly IScheduleTimeService _timeService;
        private readonly ITravelTimeResolver _travelResolver;

        public PlanServiceTaskQuery(
            ScheduleDbContext db,
            IScheduleTimeService timeService,
            ITravelTimeResolver travelResolver)
        {
            _db = db;
            _timeService = timeService;
            _travelResolver = travelResolver;
        }

        public async Task<LoadSnapshotDto> GetWorkloadSnapshotAsync(long technicianId, DateTime dayStartUtc, CancellationToken cancellationToken = default)
        {
            var technician = await _db.Technicians
                .AsNoTracking()
                .Include(t => t.WorkplaceAddress)
                .FirstOrDefaultAsync(t => t.Id == technicianId, cancellationToken);

            if (technician == null)
            {
                return new LoadSnapshotDto();
            }

            var dayEnd = dayStartUtc.AddDays(1);
            var weekStart = _timeService.GetWeekStart(dayStartUtc);
            var weekEnd = weekStart.AddDays(7);

            var weekTasks = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.ServiceObject)
                    .ThenInclude(o => o.Address)
                .Where(t => t.TechnicianId == technicianId &&
                            t.StartAt >= weekStart &&
                            t.StartAt < weekEnd)
                .OrderBy(t => t.StartAt)
                .ToListAsync(cancellationToken);

            var dayTasks = weekTasks
                .Where(t => t.StartAt >= dayStartUtc && t.StartAt < dayEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var travelMinutesByTaskId = await BuildTravelMinutesByTaskIdAsync(technician, weekTasks, cancellationToken);
            var snapshot = WorkloadCalculator.Evaluate(technician, dayTasks, weekTasks, travelMinutesByTaskId);
            return MapToLoadSnapshotDto(technician, snapshot);
        }

        internal static LoadSnapshotDto MapToLoadSnapshotDto(TechnicianEntity technician, WorkloadSnapshot snapshot)
        {
            var context = snapshot.Context;
            return new LoadSnapshotDto
            {
                LoadState = context.ComputeLoadState().ToString(),
                HasConflict = context.HasConflict,
                IsDailyLimitExceeded = context.IsDailyLimitExceeded,
                IsWeeklyLimitExceeded = context.IsWeeklyLimitExceeded,
                DailyHours = Math.Round(snapshot.DailyHours, 2),
                WeeklyHours = Math.Round(snapshot.WeeklyHours, 2),
                DailyLimit = (double)technician.DailyLimit,
                WeeklyLimit = (double)technician.WeeklyLimit,
                DailyUtilization = Math.Round(context.DailyUtilization, 2),
                WeeklyUtilization = Math.Round(context.WeeklyUtilization, 2),
                TravelTimeHours = Math.Round(context.TravelTimeHours, 2)
            };
        }

        internal async Task<Dictionary<long, int>> BuildTravelMinutesByTaskIdAsync(
            TechnicianEntity technician,
            List<TaskEntity> weekTasks,
            CancellationToken cancellationToken)
        {
            // Use centralized resolver instead of duplicating route query logic
            var addressIds = weekTasks
                .Where(t => t.ServiceObject != null)
                .Select(t => t.ServiceObject!.AddressId)
                .Prepend(technician.WorkplaceAddressId)
                .Distinct()
                .ToList();

            var travelMinutes = await _travelResolver.GetTravelMinutesAsync(addressIds, cancellationToken);

            var result = new Dictionary<long, int>();
            var tasksByDay = weekTasks
                .OrderBy(t => t.StartAt)
                .GroupBy(t => t.StartAt.Date)
                .ToList();

            foreach (var dayGroup in tasksByDay)
            {
                TaskEntity? prev = null;
                foreach (var task in dayGroup.OrderBy(t => t.StartAt))
                {
                    var fromAddressId = prev != null
                        ? prev.ServiceObject!.AddressId
                        : technician.WorkplaceAddressId;
                    var toAddressId = task.ServiceObject!.AddressId;

                    if (travelMinutes.TryGetValue((fromAddressId, toAddressId), out var travelMinute))
                    {
                        result[task.Id] = travelMinute;
                    }
                    else
                    {
                        result[task.Id] = 0;
                    }

                    prev = task;
                }
            }

            return result;
        }

        public async Task<TaskDetailResult?> GetTaskDetailAsync(long taskId, CancellationToken cancellationToken = default)
        {
            var task = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.Plan)
                .Include(t => t.ServiceObject)
                    .ThenInclude(o => o.Address)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                return null;
            }

            var version = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => EF.Property<uint>(t, "xmin"))
                .FirstAsync(cancellationToken);

            return new TaskDetailResult(task, version);
        }

        public async Task<uint> GetTaskVersionAsync(long taskId, CancellationToken cancellationToken = default)
        {
            return await _db.Tasks
                .AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => EF.Property<uint>(t, "xmin"))
                .FirstAsync(cancellationToken);
        }

        public async Task<bool> TechnicianExistsAsync(long technicianId, CancellationToken cancellationToken = default)
        {
            return await _db.Technicians
                .AsNoTracking()
                .AnyAsync(t => t.Id == technicianId, cancellationToken);
        }

        public async Task<bool> HasTaskOverlapAsync(long excludeTaskId, long technicianId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
        {
            return await _db.Tasks
                .AsNoTracking()
                .Where(t => t.Id != excludeTaskId)
                .Where(t => t.TechnicianId == technicianId)
                .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                .Where(t => t.StartAt < endUtc && t.EndAt > startUtc)
                .AnyAsync(cancellationToken);
        }

        public async Task<(int StartMinutes, int EndMinutes)> GetSuggestedNextSlotAsync(
            long technicianId,
            DateTime targetDate,
            int timezoneOffsetMinutes,
            int slotSizeMinutes,
            int dayStartMinutes,
            int? afterEndMinutes,
            long? previousServiceObjectId = null,
            long? newServiceObjectId = null,
            bool appendAfterLastTask = false,
            CancellationToken cancellationToken = default)
        {
            var earliestAllowed = _timeService.GetEarliestCreatableStartMinutes(targetDate, timezoneOffsetMinutes, slotSizeMinutes, dayStartMinutes);
            var candidate = afterEndMinutes.HasValue
                ? _timeService.CeilingToSlot(afterEndMinutes.Value, slotSizeMinutes)
                : earliestAllowed;
            candidate = Math.Max(candidate, earliestAllowed);

            var dayStartUtc = _timeService.CreateUtcFromDateAndMinutes(targetDate, 0, timezoneOffsetMinutes);
            var dayEndUtc = dayStartUtc.AddDays(1);

            var technician = await _db.Technicians
                .AsNoTracking()
                .Where(t => t.Id == technicianId)
                .Select(t => new { t.Id, t.WorkplaceAddressId })
                .FirstOrDefaultAsync(cancellationToken);

            if (technician == null)
            {
                return (candidate, candidate + slotSizeMinutes);
            }

            var existingTasks = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.ServiceObject)
                .Where(t => t.TechnicianId == technicianId)
                .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                .Where(t => t.StartAt >= dayStartUtc && t.StartAt < dayEndUtc)
                .OrderBy(t => t.StartAt)
                .ToListAsync(cancellationToken);

            long? previousAddressId = null;
            long? newAddressId = null;
            if (previousServiceObjectId.HasValue)
            {
                previousAddressId = await _db.ServiceObjects
                    .Where(o => o.Id == previousServiceObjectId.Value)
                    .Select(o => (long?)o.AddressId)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            if (newServiceObjectId.HasValue)
            {
                newAddressId = await _db.ServiceObjects
                    .Where(o => o.Id == newServiceObjectId.Value)
                    .Select(o => (long?)o.AddressId)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            if (newAddressId.HasValue)
            {
                var addressIds = existingTasks
                    .Select(t => t.ServiceObject?.AddressId ?? 0)
                    .Append(technician.WorkplaceAddressId)
                    .Append(newAddressId.Value)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();

                var routeLookup = await _travelResolver.GetTravelMinutesAsync(addressIds, cancellationToken);
                candidate = FindEarliestFeasibleCandidate(
                    candidate,
                    earliestAllowed,
                    slotSizeMinutes,
                    technician.WorkplaceAddressId,
                    previousAddressId,
                    newAddressId.Value,
                    existingTasks,
                    routeLookup,
                    timezoneOffsetMinutes,
                    appendAfterLastTask);

                return (candidate, candidate + slotSizeMinutes);
            }

            // appendAfterLastTask: push candidate past the technician's last task of the day
            // so the suggestion appends to the end of their schedule rather than filling the
            // first free gap from the current time.
            if (appendAfterLastTask && existingTasks.Count > 0)
            {
                var lastEndMins = (int)_timeService.ConvertUtcToLocal(existingTasks[^1].EndAt, timezoneOffsetMinutes).TimeOfDay.TotalMinutes;
                candidate = Math.Max(candidate, _timeService.CeilingToSlot(lastEndMins, slotSizeMinutes));
            }

            foreach (var task in existingTasks)
            {
                var taskStartMins = (int)_timeService.ConvertUtcToLocal(task.StartAt, timezoneOffsetMinutes).TimeOfDay.TotalMinutes;
                var taskEndMins   = (int)_timeService.ConvertUtcToLocal(task.EndAt,   timezoneOffsetMinutes).TimeOfDay.TotalMinutes;

                if (candidate >= taskStartMins && candidate < taskEndMins)
                {
                    candidate = _timeService.CeilingToSlot(taskEndMins, slotSizeMinutes);
                }
            }

            return (candidate, candidate + slotSizeMinutes);
        }

        private int FindEarliestFeasibleCandidate(
            int candidate,
            int earliestAllowed,
            int slotSizeMinutes,
            long workplaceAddressId,
            long? previousAddressId,
            long newAddressId,
            List<TaskEntity> existingTasks,
            IReadOnlyDictionary<(long From, long To), int> routeLookup,
            int timezoneOffsetMinutes,
            bool appendAfterLastTask)
        {
            if (appendAfterLastTask && existingTasks.Count > 0)
            {
                var lastTask = existingTasks[^1];
                var lastEndMinutes = (int)_timeService.ConvertUtcToLocal(lastTask.EndAt, timezoneOffsetMinutes).TimeOfDay.TotalMinutes;
                var fromAddressId = lastTask.ServiceObject?.AddressId ?? workplaceAddressId;
                var inboundTravel = ResolveTravelMinutes(routeLookup, fromAddressId, newAddressId);
                candidate = Math.Max(candidate, _timeService.CeilingToSlot(lastEndMinutes + inboundTravel, slotSizeMinutes));
            }

            candidate = Math.Max(candidate, earliestAllowed);

            while (true)
            {
                var previousTask = existingTasks
                    .LastOrDefault(t => LocalMinutes(t.EndAt, timezoneOffsetMinutes) <= candidate);
                var nextTask = existingTasks
                    .FirstOrDefault(t => LocalMinutes(t.StartAt, timezoneOffsetMinutes) >= candidate);

                var inboundFromAddressId = previousAddressId
                    ?? previousTask?.ServiceObject?.AddressId
                    ?? workplaceAddressId;
                var inboundTravel = ResolveTravelMinutes(routeLookup, inboundFromAddressId, newAddressId);
                var previousEndMinutes = previousTask != null
                    ? LocalMinutes(previousTask.EndAt, timezoneOffsetMinutes)
                    : ScheduleGrid.DayStartMinutes;

                var earliestFromInbound = _timeService.CeilingToSlot(
                    Math.Max(candidate, previousEndMinutes + inboundTravel),
                    slotSizeMinutes);

                if (earliestFromInbound > candidate)
                {
                    candidate = Math.Max(earliestFromInbound, earliestAllowed);
                    continue;
                }

                var overlappingTask = existingTasks.FirstOrDefault(t =>
                {
                    var taskStartMinutes = LocalMinutes(t.StartAt, timezoneOffsetMinutes);
                    var taskEndMinutes = LocalMinutes(t.EndAt, timezoneOffsetMinutes);
                    return candidate < taskEndMinutes && candidate + slotSizeMinutes > taskStartMinutes;
                });

                if (overlappingTask != null)
                {
                    candidate = Math.Max(
                        _timeService.CeilingToSlot(LocalMinutes(overlappingTask.EndAt, timezoneOffsetMinutes), slotSizeMinutes),
                        earliestAllowed);
                    continue;
                }

                if (nextTask != null)
                {
                    var nextStartMinutes = LocalMinutes(nextTask.StartAt, timezoneOffsetMinutes);
                    var outboundTravel = ResolveTravelMinutes(routeLookup, newAddressId, nextTask.ServiceObject?.AddressId ?? 0);
                    if (candidate + slotSizeMinutes + outboundTravel > nextStartMinutes)
                    {
                        candidate = Math.Max(
                            _timeService.CeilingToSlot(LocalMinutes(nextTask.EndAt, timezoneOffsetMinutes), slotSizeMinutes),
                            earliestAllowed);
                        continue;
                    }
                }

                return candidate;
            }
        }

        private int LocalMinutes(DateTime utcDateTime, int timezoneOffsetMinutes) =>
            (int)_timeService.ConvertUtcToLocal(utcDateTime, timezoneOffsetMinutes).TimeOfDay.TotalMinutes;

        private static int ResolveTravelMinutes(
            IReadOnlyDictionary<(long From, long To), int> routeLookup,
            long fromAddressId,
            long toAddressId)
        {
            if (fromAddressId <= 0 || toAddressId <= 0 || fromAddressId == toAddressId)
            {
                return 0;
            }

            return routeLookup.TryGetValue((fromAddressId, toAddressId), out var minutes)
                ? minutes
                : 0;
        }
    }
}
