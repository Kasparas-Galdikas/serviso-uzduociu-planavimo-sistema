using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.History;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator
{
    internal class PlanServiceTaskDeletion
    {
        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly IScheduleRepairService _scheduleRepairService;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly ITaskHistoryWriter _historyWriter;
        private readonly RouteEvaluationCaller _routeEvaluationCaller;
        private readonly PlanServicePlanLifecycle _planLifecycle;
        private readonly IScheduleTimeService _timeService;
        private readonly ILogger<PlanServiceTaskDeletion> _logger;

        public PlanServiceTaskDeletion(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            IScheduleRepairService scheduleRepairService,
            ITaskPermissionService taskPermissionService,
            ITaskHistoryWriter historyWriter,
            RouteEvaluationCaller routeEvaluationCaller,
            PlanServicePlanLifecycle planLifecycle,
            IScheduleTimeService timeService,
            ILogger<PlanServiceTaskDeletion> logger)
        {
            _db = db;
            _evaluationEngine = evaluationEngine;
            _scheduleRepairService = scheduleRepairService;
            _taskPermissionService = taskPermissionService;
            _historyWriter = historyWriter;
            _routeEvaluationCaller = routeEvaluationCaller;
            _planLifecycle = planLifecycle;
            _timeService = timeService;
            _logger = logger;
        }

        public async Task<TaskDeleteResult> DeleteTaskAsync(long taskId, uint expectedVersion, bool autoCompact, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("svc.task.delete.requested taskId={TaskId} expectedVersion={Version}", taskId, expectedVersion);

            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                _logger.LogWarning("svc.task.delete.not-found taskId={TaskId}", taskId);
                var _rDnf = new TaskDeleteResult(TaskDeleteError.NotFound, $"Task {taskId} was not found.");
                var (_mDnf, _oDnf) = TaskHistoryMessages.ForDelete(_rDnf, string.Empty);
                await _historyWriter.RecordAsync(taskId, null, "delete", _oDnf, _mDnf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rDnf;
            }

            var currentVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            if (expectedVersion == 0 || expectedVersion != currentVersion)
            {
                _logger.LogWarning("svc.task.delete.stale taskId={TaskId} expectedVersion={Expected} currentVersion={Current}", taskId, expectedVersion, currentVersion);
                var _rDst = new TaskDeleteResult(TaskDeleteError.Stale, "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.", CurrentVersion: currentVersion);
                var (_mDst, _oDst) = TaskHistoryMessages.ForDelete(_rDst, task.Title);
                await _historyWriter.RecordAsync(task.Id, task.PlanId, "delete", _oDst, _mDst, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rDst;
            }

            var canDelete = await _taskPermissionService.CanDeleteTaskAsync(task, user, timezoneOffsetMinutes);
            if (!canDelete)
            {
                _logger.LogWarning("svc.task.delete.not-editable taskId={TaskId}", taskId);
                var _rDne = new TaskDeleteResult(TaskDeleteError.NotEditable, "Neturite teisiu istrinti sios uzduoties.");
                var (_mDne, _oDne) = TaskHistoryMessages.ForDelete(_rDne, task.Title);
                await _historyWriter.RecordAsync(task.Id, task.PlanId, "delete", _oDne, _mDne, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rDne;
            }

            var planId = task.PlanId;
            var plan = task.Plan;
            var deletedTechnicianId = task.TechnicianId;
            var deletedDay = task.StartAt.Date;
            var deletedTitle = task.Title;
            var deletedStatus = task.Status;

            _db.Tasks.Remove(task);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogWarning("svc.task.delete.stale-on-save taskId={TaskId}", taskId);
                var _rDstDb = new TaskDeleteResult(TaskDeleteError.Stale, "Uzduotis pasikeite (stale).", CurrentVersion: currentVersion);
                var (_mDstDb, _oDstDb) = TaskHistoryMessages.ForDelete(_rDstDb, deletedTitle);
                await _historyWriter.RecordAsync(taskId, planId, "delete", _oDstDb, _mDstDb, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rDstDb;
            }

            if (autoCompact)
            {
                var compactPlan = await _scheduleRepairService.BuildPlanAsync(
                    new ScheduleRepairRequest
                    {
                        ScenarioKind = ScheduleRepairScenarioKind.Delete,
                        TechnicianId = deletedTechnicianId,
                        DayUtc = deletedDay,
                        PlanId = planId,
                        FilterByPlanId = planId.HasValue,
                        TimezoneOffsetMinutes = timezoneOffsetMinutes
                    },
                    cancellationToken);
                if (compactPlan.Success)
                {
                    await _scheduleRepairService.ApplyPlanAsync(compactPlan, null, cancellationToken);
                }
            }

            if (planId.HasValue && plan != null)
            {
                await _planLifecycle.AdjustPlanSpanAsync(plan, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(planId.Value, cancellationToken);
            }

            await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(deletedTechnicianId, deletedDay, "delete", cancellationToken);

            _logger.LogInformation("svc.task.delete.ok taskId={TaskId}", taskId);
            var _rDok = new TaskDeleteResult(TaskDeleteError.None, Task: task, CurrentVersion: 0);
            var (_mDok, _oDok) = TaskHistoryMessages.ForDelete(_rDok, deletedTitle);
            await _historyWriter.RecordAsync(taskId, planId, "delete", _oDok, _mDok, user.UserId, user.Role.ToString().ToLower(),
                new { deletedTaskId = taskId, wasStatus = deletedStatus.ToString(), wasAutoCompacted = autoCompact, technicianId = deletedTechnicianId }, cancellationToken: cancellationToken);
            return _rDok;
        }

        public async Task<(int Deleted, int Skipped)> BulkDeleteTasksAsync(long[] ids, UserContext user, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("svc.task.bulk-delete.requested count={Count}", ids.Length);

            var tasks = await _db.Tasks
                .Include(t => t.Plan)
                .Where(t => ids.Contains(t.Id))
                .ToListAsync(cancellationToken);

            var toDelete = new List<TaskEntity>();
            var skipped = 0;

            foreach (var task in tasks)
            {
                if (!_timeService.IsTaskStillEditable(task.EndAt))
                {
                    _logger.LogWarning("svc.task.bulk-delete.skipped-past taskId={TaskId}", task.Id);
                    skipped++;
                    continue;
                }
                toDelete.Add(task);
            }

            if (toDelete.Count == 0)
            {
                _logger.LogInformation("svc.task.bulk-delete.nothing-to-delete skipped={Skipped}", skipped);
                return (0, skipped);
            }

            _db.Tasks.RemoveRange(toDelete);
            await _db.SaveChangesAsync(cancellationToken);

            // Post-delete side effects — mirror single delete behaviour
            var affectedPlanIds = toDelete
                .Where(t => t.PlanId.HasValue)
                .Select(t => t.PlanId!.Value)
                .Distinct();

            var affectedTechnicianDays = toDelete
                .Select(t => (t.TechnicianId, Day: t.StartAt.Date))
                .Distinct();

            foreach (var planId in affectedPlanIds)
                await _evaluationEngine.RecalculatePlanAsync(planId, cancellationToken);

            foreach (var (technicianId, day) in affectedTechnicianDays)
                await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(technicianId, day, "bulk-delete", cancellationToken);

            var actorRole = user.Role.ToString().ToLower();
            var successResult = new TaskDeleteResult(TaskDeleteError.None, CurrentVersion: 0);
            foreach (var task in toDelete)
            {
                var (summary, outcome) = TaskHistoryMessages.ForDelete(successResult, task.Title);
                await _historyWriter.RecordAsync(
                    task.Id, task.PlanId, "delete", outcome, summary,
                    user.UserId, actorRole,
                    new { deletedTaskId = task.Id, wasStatus = task.Status.ToString(), source = "bulk" },
                    cancellationToken: cancellationToken);
            }

            _logger.LogInformation("svc.task.bulk-delete.ok deleted={Deleted} skipped={Skipped}", toDelete.Count, skipped);
            return (toDelete.Count, skipped);
        }
    }
}
