using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Interfaces;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.Authorization
{
    /// <summary>
    /// Service responsible for determining if a user can edit a task.
    /// Uses strategy pattern to delegate plan-status-specific rules to policy implementations.
    /// </summary>
    public class TaskPermissionService : ITaskPermissionService
    {
        private readonly IScheduleTimeService _timeService;
        private readonly ILogger<TaskPermissionService> _logger;
        private readonly Dictionary<PlanStatus, IPlanStatusEditPolicy> _policiesByStatus;

        public TaskPermissionService(
            IScheduleTimeService timeService,
            IEnumerable<IPlanStatusEditPolicy> policies,
            ILogger<TaskPermissionService> logger)
        {
            _timeService = timeService;
            _logger = logger;

            // Build dictionary for O(1) policy lookup by plan status
            _policiesByStatus = policies.ToDictionary(p => p.AppliesTo);
        }

        public Task<bool> CanEditTaskAsync(TaskEntity task, UserContext user, int timezoneOffsetMinutes)
        {
            var (canEdit, reason) = EvaluatePermission(task, user, timezoneOffsetMinutes);

            _logger.LogDebug(
                "Authorization decision: User={UserId} Tech={TechnicianId} Role={Role} Task={TaskId} " +
                "PlanStatus={PlanStatus} Decision={Decision} Reason={Reason}",
                user.UserId, user.TechnicianId, user.Role, task.Id, task.Plan?.Status,
                canEdit, reason);

            return Task.FromResult(canEdit);
        }

        public Task<bool> CanDeleteTaskAsync(TaskEntity task, UserContext user, int timezoneOffsetMinutes)
        {
            var (canDelete, reason) = EvaluateDeletePermission(task, user, timezoneOffsetMinutes);

            _logger.LogDebug(
                "Authorization decision: User={UserId} Tech={TechnicianId} Role={Role} Task={TaskId} " +
                "PlanStatus={PlanStatus} Decision={Decision} Reason={Reason} Action=Delete",
                user.UserId, user.TechnicianId, user.Role, task.Id, task.Plan?.Status,
                canDelete, reason);

            return Task.FromResult(canDelete);
        }

        private (bool CanEdit, string Reason) EvaluatePermission(TaskEntity task, UserContext user, int timezoneOffsetMinutes)
        {
            // 1. UTC-based time validation (SECURITY: prevent clock manipulation)
            // Check if task ACTUALLY ended in real time (server UTC reality)
            if (!_timeService.IsTaskStillEditable(task.EndAt))
            {
                return (false, "Task has already ended (UTC reality check)");
            }

            // 1.5. Status-based lockdown: once technician changes status from InProgress, task is locked
            if (task.Status != ScheduleTaskStatus.InProgress)
            {
                return (false, "Task status has been changed and is now locked");
            }

            // 2. Defensive guard for unexpected roles (only managers/technicians are supported)
            var hasKnownRole = user.IsManager || user.IsTechnician;
            if (!hasKnownRole)
            {
                return (false, "Unsupported role");
            }
            if (user.IsTechnician && !user.TechnicianId.HasValue)
            {
                return (false, "Technician mapping missing");
            }

            // 3. Calculate local time context for plan status policies
            var userLocalToday = _timeService.GetUserLocalToday(timezoneOffsetMinutes);
            var taskStartLocal = _timeService.ConvertUtcToLocal(task.StartAt, timezoneOffsetMinutes);
            var taskLocalDate = taskStartLocal.Date;

            // 4. Delegate to plan-status-specific policy (Strategy Pattern)
            var planStatus = task.Plan?.Status ?? PlanStatus.Active;

            if (!_policiesByStatus.TryGetValue(planStatus, out var policy))
            {
                // No policy registered for this plan status - deny by default
                return (false, $"No edit policy defined for plan status: {planStatus}");
            }

            // Execute the strategy
            var allowed = policy.CanEdit(task, user, taskLocalDate, userLocalToday);

            if (!allowed)
            {
                return (false, $"Denied by {policy.GetType().Name} (plan status: {planStatus})");
            }

            return (true, $"Approved by {policy.GetType().Name}");
        }

        private (bool CanDelete, string Reason) EvaluateDeletePermission(TaskEntity task, UserContext user, int timezoneOffsetMinutes)
        {
            // Only managers can delete tasks.
            if (!user.IsManager)
            {
                return (false, "Only managers can delete tasks");
            }

            // Keep time-based guard consistent with UI (no deletes after task end).
            if (!_timeService.IsTaskStillEditable(task.EndAt))
            {
                return (false, "Task has already ended (UTC reality check)");
            }

            return (true, "Approved for manager delete");
        }
    }
}
