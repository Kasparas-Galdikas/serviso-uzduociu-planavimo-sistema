using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.Authorization
{
    public class TaskPermissionService : ITaskPermissionService
    {
        private readonly IScheduleTimeService _timeService;

        public TaskPermissionService(IScheduleTimeService timeService)
        {
            _timeService = timeService;
        }

        public Task<bool> CanEditTaskAsync(TaskEntity task, UserContext user, int timezoneOffsetMinutes)
        {
            // 1. Role check
            if (!user.IsManager) return Task.FromResult(false);

            // 2. UTC-based time validation (SECURITY: prevent clock manipulation)
            var userLocalToday = _timeService.GetUserLocalToday(timezoneOffsetMinutes);
            var taskStartLocal = _timeService.ConvertUtcToLocal(task.StartAt, timezoneOffsetMinutes);
            var taskLocalDate = taskStartLocal.Date;

            // Check if task ACTUALLY ended (UTC reality check)
            if (!_timeService.IsTaskStillEditable(task.EndAt))
            {
                return Task.FromResult(false); // Task finished in real time
            }

            // 3. Plan-specific rules
            var planStatus = task.Plan?.Status ?? PlanStatus.Active;

            if (planStatus == PlanStatus.Draft)
            {
                // Draft tasks: ALWAYS editable (staging area, no time restriction)
                return Task.FromResult(true);
            }

            if (planStatus == PlanStatus.Active || planStatus == PlanStatus.Superseded)
            {
                // UNIFIED LOGIC: Both Active and Superseded use same time-based rule
                // REMOVED: Draft-blocking check (allow editing Active even if Draft exists)
                if (taskLocalDate < userLocalToday)
                {
                    return Task.FromResult(false); // Task is in the past (local perspective)
                }
                return Task.FromResult(true); // Future task, editable
            }

            return Task.FromResult(false);
        }
    }
}
