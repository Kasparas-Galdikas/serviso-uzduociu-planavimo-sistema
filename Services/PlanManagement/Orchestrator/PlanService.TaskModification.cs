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
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.History;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator
{
    internal class PlanServiceTaskModification
    {
        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly IScheduleTimeService _timeService;
        private readonly IScheduleRepairService _scheduleRepairService;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly ITaskHistoryWriter _historyWriter;
        private readonly SchedulePlacementValidator _placementValidator;
        private readonly ServiceLocationResolver _locationResolver;
        private readonly RouteEvaluationCaller _routeEvaluationCaller;
        private readonly PlanServicePlanLifecycle _planLifecycle;
        private readonly ITaskTravelFeasibilityService _travelFeasibilityService;
        private readonly ILogger<PlanServiceTaskModification> _logger;

        public PlanServiceTaskModification(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            IScheduleTimeService timeService,
            IScheduleRepairService scheduleRepairService,
            ITaskPermissionService taskPermissionService,
            ITaskHistoryWriter historyWriter,
            SchedulePlacementValidator placementValidator,
            ServiceLocationResolver locationResolver,
            RouteEvaluationCaller routeEvaluationCaller,
            PlanServicePlanLifecycle planLifecycle,
            ITaskTravelFeasibilityService travelFeasibilityService,
            ILogger<PlanServiceTaskModification> logger)
        {
            _db = db;
            _evaluationEngine = evaluationEngine;
            _timeService = timeService;
            _scheduleRepairService = scheduleRepairService;
            _taskPermissionService = taskPermissionService;
            _historyWriter = historyWriter;
            _placementValidator = placementValidator;
            _locationResolver = locationResolver;
            _routeEvaluationCaller = routeEvaluationCaller;
            _planLifecycle = planLifecycle;
            _travelFeasibilityService = travelFeasibilityService;
            _logger = logger;
        }

        public async Task<TaskUpdateResult> UpdateTaskAsync(long taskId, Action<TaskEntity> applyChanges, long technicianId, UserContext user, int timezoneOffsetMinutes, string? objectName, string? objectAddress, CancellationToken cancellationToken = default)
        {
            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                var _rNf = new TaskUpdateResult(TaskUpdateError.NotFound, "Uzduotis nerasta.");
                var (_mNf, _oNf) = TaskHistoryMessages.ForUpdate(_rNf, string.Empty);
                await _historyWriter.RecordAsync(taskId, null, "update", _oNf, _mNf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rNf;
            }

            var canEdit = await _taskPermissionService.CanEditTaskAsync(task, user, timezoneOffsetMinutes);
            if (!canEdit)
            {
                var _rNe = new TaskUpdateResult(TaskUpdateError.NotEditable, "Neturite teisiu redaguoti sios uzduoties.");
                var (_mNe, _oNe) = TaskHistoryMessages.ForUpdate(_rNe, task.Title);
                await _historyWriter.RecordAsync(taskId, task.PlanId, "update", _oNe, _mNe, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rNe;
            }

            if (technicianId != task.TechnicianId)
            {
                var targetTech = await _db.Technicians
                    .AsNoTracking()
                    .Select(t => new { t.Id })
                    .FirstOrDefaultAsync(t => t.Id == technicianId, cancellationToken);

                if (targetTech == null)
                {
                    var _rTnf = new TaskUpdateResult(TaskUpdateError.TechnicianNotFound, "Technikas nerastas.");
                    var (_mTnf, _oTnf) = TaskHistoryMessages.ForUpdate(_rTnf, task.Title);
                    await _historyWriter.RecordAsync(taskId, task.PlanId, "update", _oTnf, _mTnf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                    return _rTnf;
                }

                task.TechnicianId = targetTech.Id;
            }

            await using var transaction = await _placementValidator.BeginTransactionIfSupportedAsync(cancellationToken);

            applyChanges(task);

            if (!string.IsNullOrWhiteSpace(objectName) || !string.IsNullOrWhiteSpace(objectAddress))
            {
                var city = await _db.Technicians
                    .Where(t => t.Id == task.TechnicianId)
                    .Select(t => t.WorkplaceAddress.City)
                    .FirstAsync(cancellationToken);
                var serviceObjectId = await _locationResolver.FindOrCreateServiceObjectAsync(objectName, objectAddress, city, cancellationToken);
                task.ServiceObjectId = serviceObjectId;
            }
            var localPlacementStartAt = _timeService.ConvertUtcToLocal(task.StartAt, timezoneOffsetMinutes);
            var localPlacementEndAt = _timeService.ConvertUtcToLocal(task.EndAt, timezoneOffsetMinutes);
            var updatePlacementValidation = await _placementValidator.ValidatePlacementForActionAsync(
                ignoreTaskIds: [taskId],
                targetTechnicianId: task.TechnicianId,
                targetDate: localPlacementStartAt.Date,
                startMinutes: (int)localPlacementStartAt.TimeOfDay.TotalMinutes,
                endMinutes: (int)localPlacementEndAt.TimeOfDay.TotalMinutes,
                plan: task.Plan,
                status: task.Status,
                user: user,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                requireEndAligned: true,
                cancellationToken: cancellationToken);

            if (!updatePlacementValidation.IsValid)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                var _rPl = SchedulePlacementValidator.MapPlacementErrorForUpdate(updatePlacementValidation);
                var (_mPl, _oPl) = TaskHistoryMessages.ForUpdate(_rPl, task.Title);
                await _historyWriter.RecordAsync(taskId, task.PlanId, "update", _oPl, _mPl, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rPl;
            }

            task.StartAt = updatePlacementValidation.StartAtUtc;
            task.EndAt = updatePlacementValidation.EndAtUtc;

            await _db.SaveChangesAsync(cancellationToken);
            await _routeEvaluationCaller.TriggerTaskRouteImpactSafelyAsync(task.Id, "update", cancellationToken);
            await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "update", cancellationToken);

            var finalAddressId = await _db.ServiceObjects
                .AsNoTracking()
                .Where(o => o.Id == task.ServiceObjectId)
                .Select(o => o.AddressId)
                .FirstAsync(cancellationToken);
            var localStartAt = _timeService.ConvertUtcToLocal(task.StartAt, timezoneOffsetMinutes);
            var startMinutesLocal = (int)localStartAt.TimeOfDay.TotalMinutes;
            var timingTravelValidation = await _placementValidator.ValidateTimingAndTravelAsync(
                taskDateLocal: localStartAt.Date,
                startMinutesLocal: startMinutesLocal,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                technicianId: task.TechnicianId,
                startAtUtc: task.StartAt,
                endAtUtc: task.EndAt,
                serviceAddressId: finalAddressId,
                excludeTaskId: taskId,
                cancellationToken: cancellationToken);
            if (!timingTravelValidation.IsValid)
            {
                if (!timingTravelValidation.IsInvalidWindow && await _placementValidator.TryApplyTimingTravelRepairAsync(task, timingTravelValidation, cancellationToken))
                {
                    await _routeEvaluationCaller.TriggerTaskRouteImpactSafelyAsync(task.Id, "update-repair", cancellationToken);
                    await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "update-repair", cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    if (task.PlanId.HasValue)
                    {
                        await _planLifecycle.AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                        await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
                    }

                    var _rRep = new TaskUpdateResult(
                        TaskUpdateError.None,
                        _placementValidator.BuildRepairAppliedMessage(timingTravelValidation.Message, timingTravelValidation.RepairPlan?.PrimaryPlacement, timezoneOffsetMinutes),
                        task);
                    var (_mRep, _oRep) = TaskHistoryMessages.ForUpdate(_rRep, task.Title);
                    await _historyWriter.RecordAsync(taskId, task.PlanId, "update", _oRep, _mRep, user.UserId, user.Role.ToString().ToLower(),
                        new { technicianId = task.TechnicianId, repairApplied = true }, cancellationToken: cancellationToken);
                    return _rRep;
                }

                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                var _rTt = new TaskUpdateResult(timingTravelValidation.IsInvalidWindow ? TaskUpdateError.InvalidWindow : TaskUpdateError.TravelInfeasible, timingTravelValidation.Message);
                var (_mTt, _oTt) = TaskHistoryMessages.ForUpdate(_rTt, task.Title);
                await _historyWriter.RecordAsync(taskId, task.PlanId, "update", _oTt, _mTt, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rTt;
            }
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            if (task.PlanId.HasValue)
            {
                await _planLifecycle.AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
            }

            var _rOk = new TaskUpdateResult(TaskUpdateError.None, Task: task);
            var (_mOk, _oOk) = TaskHistoryMessages.ForUpdate(_rOk, task.Title);
            await _historyWriter.RecordAsync(taskId, task.PlanId, "update", _oOk, _mOk, user.UserId, user.Role.ToString().ToLower(),
                new { technicianId = task.TechnicianId }, cancellationToken: cancellationToken);
            return _rOk;
        }

        public async Task<TaskTimeUpdateResult> UpdateTaskTimeAsync(long taskId, DateTime targetDate, int targetSlotStartMinutes, int targetSlotEndMinutes, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default)
        {
            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                var _rNf = new TaskTimeUpdateResult(TaskTimeUpdateError.NotFound, $"Task {taskId} was not found.");
                var (_mNf, _oNf) = TaskHistoryMessages.ForReschedule(_rNf, string.Empty);
                await _historyWriter.RecordAsync(taskId, null, "reschedule", _oNf, _mNf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rNf;
            }

            var currentVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            if (expectedVersion == 0 || expectedVersion != currentVersion)
            {
                var _rSt = new TaskTimeUpdateResult(TaskTimeUpdateError.Stale, "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.", CurrentVersion: currentVersion);
                var (_mSt, _oSt) = TaskHistoryMessages.ForReschedule(_rSt, task.Title);
                await _historyWriter.RecordAsync(task.Id, task.PlanId, "reschedule", _oSt, _mSt, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rSt;
            }

            if (targetSlotEndMinutes <= targetSlotStartMinutes)
            {
                var _rIw = new TaskTimeUpdateResult(TaskTimeUpdateError.InvalidWindow, "Pabaiga turi buti veliau nei pradzia.");
                var (_mIw, _oIw) = TaskHistoryMessages.ForReschedule(_rIw, task.Title);
                await _historyWriter.RecordAsync(task.Id, task.PlanId, "reschedule", _oIw, _mIw, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rIw;
            }

            var canEdit = await _taskPermissionService.CanEditTaskAsync(task, user, timezoneOffsetMinutes);
            if (!canEdit)
            {
                var _rNe = new TaskTimeUpdateResult(TaskTimeUpdateError.NotEditable, "Neturite teisiu keisti uzduoties laiko.");
                var (_mNe, _oNe) = TaskHistoryMessages.ForReschedule(_rNe, task.Title);
                await _historyWriter.RecordAsync(task.Id, task.PlanId, "reschedule", _oNe, _mNe, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rNe;
            }

            var placementValidation = await _placementValidator.ValidatePlacementForActionAsync(
                ignoreTaskIds: [task.Id],
                targetTechnicianId: task.TechnicianId,
                targetDate: targetDate,
                startMinutes: targetSlotStartMinutes,
                endMinutes: targetSlotEndMinutes,
                plan: task.Plan,
                status: task.Status,
                user: user,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                requireEndAligned: true,
                cancellationToken: cancellationToken);

            if (!placementValidation.IsValid)
            {
                var _rPl = SchedulePlacementValidator.MapPlacementErrorForTimeUpdate(placementValidation);
                var (_mPl, _oPl) = TaskHistoryMessages.ForReschedule(_rPl, task.Title);
                await _historyWriter.RecordAsync(task.Id, task.PlanId, "reschedule", _oPl, _mPl, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rPl;
            }

            var oldStartAt = task.StartAt;
            var oldEndAt = task.EndAt;
            var oldDay = task.StartAt.Date;

            task.StartAt = placementValidation.StartAtUtc;
            task.EndAt = placementValidation.EndAtUtc;

            await using var transaction = await _placementValidator.BeginTransactionIfSupportedAsync(cancellationToken);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                var _rStDb = new TaskTimeUpdateResult(TaskTimeUpdateError.Stale, "Uzduotis pasikeite (stale).", CurrentVersion: currentVersion);
                var (_mStDb, _oStDb) = TaskHistoryMessages.ForReschedule(_rStDb, task.Title);
                await _historyWriter.RecordAsync(task.Id, task.PlanId, "reschedule", _oStDb, _mStDb, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rStDb;
            }

            await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "reschedule", cancellationToken);

            var timeServiceAddressId = await _db.ServiceObjects
                .Where(o => o.Id == task.ServiceObjectId)
                .Select(o => o.AddressId)
                .FirstAsync(cancellationToken);
            var timeTimingTravelValidation = await _placementValidator.ValidateTimingAndTravelAsync(
                taskDateLocal: targetDate.Date,
                startMinutesLocal: targetSlotStartMinutes,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                technicianId: task.TechnicianId,
                startAtUtc: placementValidation.StartAtUtc,
                endAtUtc: placementValidation.EndAtUtc,
                serviceAddressId: timeServiceAddressId,
                excludeTaskId: task.Id,
                cancellationToken: cancellationToken);
            if (!timeTimingTravelValidation.IsValid)
            {
                if (!timeTimingTravelValidation.IsInvalidWindow && await _placementValidator.TryApplyTimingTravelRepairAsync(task, timeTimingTravelValidation, cancellationToken))
                {
                    await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "reschedule-repair", cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    if (task.PlanId.HasValue)
                    {
                        await _planLifecycle.AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                        await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
                    }

                    if (targetDate.Date != oldDay)
                    {
                        await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(task.TechnicianId, oldDay, "reschedule-old-day", cancellationToken);
                    }

                    var repairedVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
                    var _rRep = new TaskTimeUpdateResult(
                        TaskTimeUpdateError.None,
                        _placementValidator.BuildRepairAppliedMessage(timeTimingTravelValidation.Message, timeTimingTravelValidation.RepairPlan?.PrimaryPlacement, timezoneOffsetMinutes),
                        task,
                        repairedVersion,
                        oldStartAt,
                        oldEndAt);
                    var (_mRep, _oRep) = TaskHistoryMessages.ForReschedule(_rRep, task.Title,
                        oldStartAt, oldEndAt, task.StartAt, task.EndAt, timezoneOffsetMinutes);
                    await _historyWriter.RecordAsync(task.Id, task.PlanId, "reschedule", _oRep, _mRep, user.UserId, user.Role.ToString().ToLower(),
                        new { oldStartAt, oldEndAt, newStartAt = task.StartAt, newEndAt = task.EndAt, repairApplied = true }, cancellationToken: cancellationToken);
                    return _rRep;
                }

                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                var _rTr = new TaskTimeUpdateResult(
                    timeTimingTravelValidation.IsInvalidWindow ? TaskTimeUpdateError.InvalidWindow : TaskTimeUpdateError.TravelInfeasible,
                    timeTimingTravelValidation.Message);
                var (_mTr, _oTr) = TaskHistoryMessages.ForReschedule(_rTr, task.Title);
                await _historyWriter.RecordAsync(task.Id, task.PlanId, "reschedule", _oTr, _mTr, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rTr;
            }
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            if (task.PlanId.HasValue)
            {
                await _planLifecycle.AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
            }

            if (targetDate.Date != oldDay)
            {
                await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(task.TechnicianId, oldDay, "reschedule-old-day", cancellationToken);
            }

            var newVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            var _rOk = new TaskTimeUpdateResult(TaskTimeUpdateError.None, Task: task, CurrentVersion: newVersion, OldStartAt: oldStartAt, OldEndAt: oldEndAt);
            var (_mOk, _oOk) = TaskHistoryMessages.ForReschedule(_rOk, task.Title,
                oldStartAt, oldEndAt, task.StartAt, task.EndAt, timezoneOffsetMinutes);
            await _historyWriter.RecordAsync(task.Id, task.PlanId, "reschedule", _oOk, _mOk, user.UserId, user.Role.ToString().ToLower(),
                new { oldStartAt, oldEndAt, newStartAt = task.StartAt, newEndAt = task.EndAt }, cancellationToken: cancellationToken);
            return _rOk;
        }

        public async Task<TaskMoveResult> MoveTaskAsync(long taskId, long targetTechnicianId, DateTime targetDate, int targetSlotStartMinutes, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default)
        {
            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                var _rNf = new TaskMoveResult(TaskMoveError.NotFound, $"Task {taskId} was not found.");
                var (_mNf, _oNf) = TaskHistoryMessages.ForMove(_rNf, string.Empty);
                await _historyWriter.RecordAsync(taskId, null, "move", _oNf, _mNf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rNf;
            }

            var currentVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            if (expectedVersion == 0 || expectedVersion != currentVersion)
            {
                var _rSt = new TaskMoveResult(TaskMoveError.Stale, "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.", CurrentVersion: currentVersion);
                var (_mSt, _oSt) = TaskHistoryMessages.ForMove(_rSt, task.Title);
                await _historyWriter.RecordAsync(taskId, task.PlanId, "move", _oSt, _mSt, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rSt;
            }

            var duration = task.EndAt - task.StartAt;
            var durationMinutesRaw = duration.TotalMinutes;
            if (durationMinutesRaw <= 0 || Math.Abs(durationMinutesRaw - Math.Round(durationMinutesRaw)) > 0.0001)
            {
                var _rIw = new TaskMoveResult(TaskMoveError.InvalidWindow, "Netinkama uzduoties trukme.");
                var (_mIw, _oIw) = TaskHistoryMessages.ForMove(_rIw, task.Title);
                await _historyWriter.RecordAsync(taskId, task.PlanId, "move", _oIw, _mIw, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rIw;
            }

            var durationMinutes = (int)Math.Round(durationMinutesRaw);
            var endMinutes = targetSlotStartMinutes + durationMinutes;
            var placementValidation = await _placementValidator.ValidatePlacementForActionAsync(
                ignoreTaskIds: [taskId],
                targetTechnicianId: targetTechnicianId,
                targetDate: targetDate,
                startMinutes: targetSlotStartMinutes,
                endMinutes: endMinutes,
                plan: task.Plan,
                status: task.Status,
                user: user,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                requireEndAligned: false,
                cancellationToken: cancellationToken);

            if (!placementValidation.IsValid)
            {
                var _rPl = SchedulePlacementValidator.MapPlacementErrorForMove(placementValidation);
                var (_mPl, _oPl) = TaskHistoryMessages.ForMove(_rPl, task.Title);
                await _historyWriter.RecordAsync(taskId, task.PlanId, "move", _oPl, _mPl, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rPl;
            }

            var oldTechnicianId = task.TechnicianId;
            var oldStartAt = task.StartAt;
            var oldDay = task.StartAt.Date;
            var targetTechnicianProjection = oldTechnicianId == targetTechnicianId
                ? null
                : await _db.Technicians
                    .AsNoTracking()
                    .Where(t => t.Id == targetTechnicianId)
                    .Select(t => new { UserName = t.User != null ? t.User.Name : null })
                    .FirstOrDefaultAsync(cancellationToken);
            var targetTechnicianName = !string.IsNullOrWhiteSpace(targetTechnicianProjection?.UserName)
                ? targetTechnicianProjection.UserName
                : null;

            task.TechnicianId = targetTechnicianId;
            task.StartAt = placementValidation.StartAtUtc;
            task.EndAt = placementValidation.EndAtUtc;

            await using var transaction = await _placementValidator.BeginTransactionIfSupportedAsync(cancellationToken);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                var _rDbSt = new TaskMoveResult(TaskMoveError.Stale, "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.", CurrentVersion: currentVersion);
                var (_mDbSt, _oDbSt) = TaskHistoryMessages.ForMove(_rDbSt, task.Title);
                await _historyWriter.RecordAsync(taskId, task.PlanId, "move", _oDbSt, _mDbSt, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rDbSt;
            }

            var moveServiceAddressId = await _db.ServiceObjects
                .Where(o => o.Id == task.ServiceObjectId)
                .Select(o => o.AddressId)
                .FirstAsync(cancellationToken);
            var moveTimingTravelValidation = await _placementValidator.ValidateTimingAndTravelAsync(
                taskDateLocal: targetDate.Date,
                startMinutesLocal: targetSlotStartMinutes,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                technicianId: targetTechnicianId,
                startAtUtc: placementValidation.StartAtUtc,
                endAtUtc: placementValidation.EndAtUtc,
                serviceAddressId: moveServiceAddressId,
                excludeTaskId: taskId,
                cancellationToken: cancellationToken);
            if (!moveTimingTravelValidation.IsValid)
            {
                if (!moveTimingTravelValidation.IsInvalidWindow && await _placementValidator.TryApplyTimingTravelRepairAsync(task, moveTimingTravelValidation, cancellationToken))
                {
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }
                    await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "move-repair", cancellationToken);

                    if (task.PlanId.HasValue)
                    {
                        await _planLifecycle.AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                        await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
                    }

                    if (targetTechnicianId != oldTechnicianId || targetDate.Date != oldDay)
                    {
                        await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(oldTechnicianId, oldDay, "move-old-day", cancellationToken);
                    }

                    var _rRepair = new TaskMoveResult(
                        TaskMoveError.None,
                        _placementValidator.BuildRepairAppliedMessage(moveTimingTravelValidation.Message, moveTimingTravelValidation.RepairPlan?.PrimaryPlacement, timezoneOffsetMinutes),
                        task,
                        _db.Entry(task).Property<uint>("xmin").CurrentValue,
                        oldStartAt,
                        oldTechnicianId,
                        targetTechnicianName);
                    var (_mRepair, _oRepair) = TaskHistoryMessages.ForMove(_rRepair, task.Title,
                        oldStartAt, task.StartAt, timezoneOffsetMinutes, technicianChanged: oldTechnicianId != task.TechnicianId,
                        newTechnicianName: targetTechnicianName);
                    await _historyWriter.RecordAsync(taskId, task.PlanId, "move", _oRepair, _mRepair, user.UserId, user.Role.ToString().ToLower(),
                        new { oldTechnicianId, newTechnicianId = task.TechnicianId, newTechnicianName = targetTechnicianName, oldStartAt, newStartAt = task.StartAt, repairApplied = true }, cancellationToken: cancellationToken);
                    return _rRepair;
                }

                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                var _rTt = new TaskMoveResult(
                    moveTimingTravelValidation.IsInvalidWindow ? TaskMoveError.InvalidWindow : TaskMoveError.TravelInfeasible,
                    moveTimingTravelValidation.Message);
                var (_mTt, _oTt) = TaskHistoryMessages.ForMove(_rTt, task.Title);
                await _historyWriter.RecordAsync(taskId, task.PlanId, "move", _oTt, _mTt, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rTt;
            }
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "move", cancellationToken);

            if (task.PlanId.HasValue)
            {
                await _planLifecycle.AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
            }

            if (targetTechnicianId != oldTechnicianId || targetDate.Date != oldDay)
            {
                await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(oldTechnicianId, oldDay, "move-old-day", cancellationToken);
            }

            var _rOk = new TaskMoveResult(TaskMoveError.None, Task: task, CurrentVersion: _db.Entry(task).Property<uint>("xmin").CurrentValue, OldStartAt: oldStartAt, OldTechnicianId: oldTechnicianId, NewTechnicianName: targetTechnicianName);
            var (_mOk, _oOk) = TaskHistoryMessages.ForMove(_rOk, task.Title,
                oldStartAt, task.StartAt, timezoneOffsetMinutes, technicianChanged: oldTechnicianId != task.TechnicianId,
                newTechnicianName: targetTechnicianName);
            await _historyWriter.RecordAsync(taskId, task.PlanId, "move", _oOk, _mOk, user.UserId, user.Role.ToString().ToLower(),
                new { oldTechnicianId, newTechnicianId = task.TechnicianId, newTechnicianName = targetTechnicianName, oldStartAt, newStartAt = task.StartAt }, cancellationToken: cancellationToken);
            return _rOk;
        }

        public async Task<TaskSwapResult> SwapTasksAsync(
            long sourceTaskId,
            long targetTaskId,
            uint sourceExpectedVersion,
            uint targetExpectedVersion,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default,
            bool recordHistory = true)
        {
            ITaskHistoryWriter hw = recordHistory ? _historyWriter : new NoopTaskHistoryWriter();

            if (sourceTaskId == targetTaskId)
            {
                var _rSelf = new TaskSwapResult(TaskSwapError.InvalidSwap, "Negalima sukeisti uzduoties su pacia savimi.");
                var (_mSelf, _oSelf) = TaskHistoryMessages.ForSwap(_rSelf, string.Empty, string.Empty);
                await hw.RecordAsync(sourceTaskId, null, "swap", _oSelf, _mSelf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rSelf;
            }

            var source = await _db.Tasks
                .Include(t => t.Plan)
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == sourceTaskId, cancellationToken);
            if (source == null)
            {
                var _rSnf = new TaskSwapResult(TaskSwapError.NotFound, $"Task {sourceTaskId} was not found.");
                var (_mSnf, _oSnf) = TaskHistoryMessages.ForSwap(_rSnf, string.Empty, string.Empty);
                await hw.RecordAsync(sourceTaskId, null, "swap", _oSnf, _mSnf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rSnf;
            }

            var target = await _db.Tasks
                .Include(t => t.Plan)
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == targetTaskId, cancellationToken);
            if (target == null)
            {
                var _rTnf = new TaskSwapResult(TaskSwapError.TargetNotFound, $"Task {targetTaskId} was not found.");
                var (_mTnf, _oTnf) = TaskHistoryMessages.ForSwap(_rTnf, source.Title, string.Empty);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oTnf, _mTnf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rTnf;
            }

            var sourceCurrentVersion = _db.Entry(source).Property<uint>("xmin").CurrentValue;
            var targetCurrentVersion = _db.Entry(target).Property<uint>("xmin").CurrentValue;
            if (sourceExpectedVersion == 0 || sourceExpectedVersion != sourceCurrentVersion ||
                targetExpectedVersion == 0 || targetExpectedVersion != targetCurrentVersion)
            {
                var _rSt = new TaskSwapResult(
                    TaskSwapError.Stale,
                    "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.",
                    CurrentSourceVersion: sourceCurrentVersion,
                    CurrentTargetVersion: targetCurrentVersion);
                var (_mSt, _oSt) = TaskHistoryMessages.ForSwap(_rSt, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oSt, _mSt, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rSt;
            }

            var canEditSource = await _taskPermissionService.CanEditTaskAsync(source, user, timezoneOffsetMinutes);
            var canEditTarget = await _taskPermissionService.CanEditTaskAsync(target, user, timezoneOffsetMinutes);
            if (!canEditSource || !canEditTarget)
            {
                var _rNe = new TaskSwapResult(TaskSwapError.NotEditable, "Neturite teisiu sukeisti siu uzduociu.");
                var (_mNe, _oNe) = TaskHistoryMessages.ForSwap(_rNe, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oNe, _mNe, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rNe;
            }

            if (source.TechnicianId != target.TechnicianId)
            {
                var _rDt = new TaskSwapResult(TaskSwapError.InvalidSwap, "Sukeitimas galimas tik to paties techniko uzduotims.");
                var (_mDt, _oDt) = TaskHistoryMessages.ForSwap(_rDt, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oDt, _mDt, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rDt;
            }

            if (source.StartAt.Date != target.StartAt.Date)
            {
                var _rDd = new TaskSwapResult(TaskSwapError.InvalidSwap, "Sukeitimas galimas tik tame paciame dienos grafike.");
                var (_mDd, _oDd) = TaskHistoryMessages.ForSwap(_rDd, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oDd, _mDd, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rDd;
            }

            if (source.PlanId != target.PlanId)
            {
                var _rDp = new TaskSwapResult(TaskSwapError.InvalidSwap, "Sukeitimas galimas tik to paties plano viduje.");
                var (_mDp, _oDp) = TaskHistoryMessages.ForSwap(_rDp, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oDp, _mDp, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rDp;
            }

            if (source.Status == ScheduleTaskStatus.Canceled || target.Status == ScheduleTaskStatus.Canceled)
            {
                var _rCan = new TaskSwapResult(TaskSwapError.InvalidSwap, "Atsauktu uzduociu sukeisti negalima.");
                var (_mCan, _oCan) = TaskHistoryMessages.ForSwap(_rCan, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oCan, _mCan, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rCan;
            }

            var sourceDuration = source.EndAt - source.StartAt;
            var targetDuration = target.EndAt - target.StartAt;
            if (sourceDuration <= TimeSpan.Zero || targetDuration <= TimeSpan.Zero)
            {
                var _rDur = new TaskSwapResult(TaskSwapError.InvalidSwap, "Netinkama uzduoties trukme.");
                var (_mDur, _oDur) = TaskHistoryMessages.ForSwap(_rDur, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oDur, _mDur, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rDur;
            }

            var originalSourceStartAt = source.StartAt;
            var originalSourceEndAt = source.EndAt;
            var originalTargetStartAt = target.StartAt;
            var originalTargetEndAt = target.EndAt;

            var sourceNewStartAt = target.StartAt;
            var sourceNewEndAt = sourceNewStartAt + sourceDuration;
            var targetNewStartAt = source.StartAt;
            var targetNewEndAt = targetNewStartAt + targetDuration;

            var sourceNewStartLocal = sourceNewStartAt.AddMinutes(timezoneOffsetMinutes);
            var sourceNewEndLocal = sourceNewEndAt.AddMinutes(timezoneOffsetMinutes);
            var targetNewStartLocal = targetNewStartAt.AddMinutes(timezoneOffsetMinutes);
            var targetNewEndLocal = targetNewEndAt.AddMinutes(timezoneOffsetMinutes);

            if (sourceNewStartLocal.Date != sourceNewEndLocal.Date || targetNewStartLocal.Date != targetNewEndLocal.Date)
            {
                var _rMn = new TaskSwapResult(TaskSwapError.InvalidSwap, "Uzduotis turi tilpti viena diena.",
                    InvalidSwapReason: InvalidSwapReason.SpansMidnight);
                var (_mMn, _oMn) = TaskHistoryMessages.ForSwap(_rMn, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oMn, _mMn, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rMn;
            }

            var sourceStartMinutes = (int)sourceNewStartLocal.TimeOfDay.TotalMinutes;
            var sourceEndMinutes = (int)sourceNewEndLocal.TimeOfDay.TotalMinutes;
            var targetStartMinutes = (int)targetNewStartLocal.TimeOfDay.TotalMinutes;
            var targetEndMinutes = (int)targetNewEndLocal.TimeOfDay.TotalMinutes;

            var excludeIds = new[] { source.Id, target.Id };

            var sourcePlacement = await _placementValidator.ValidatePlacementForActionAsync(
                ignoreTaskIds: excludeIds,
                targetTechnicianId: source.TechnicianId,
                targetDate: sourceNewStartLocal.Date,
                startMinutes: sourceStartMinutes,
                endMinutes: sourceEndMinutes,
                plan: source.Plan,
                status: source.Status,
                user: user,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                requireEndAligned: false,
                cancellationToken: cancellationToken);
            if (!sourcePlacement.IsValid)
            {
                var _rSpl = SchedulePlacementValidator.MapPlacementErrorForSwap(sourcePlacement);
                var (_mSpl, _oSpl) = TaskHistoryMessages.ForSwap(_rSpl, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oSpl, _mSpl, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rSpl;
            }

            var targetPlacement = await _placementValidator.ValidatePlacementForActionAsync(
                ignoreTaskIds: excludeIds,
                targetTechnicianId: target.TechnicianId,
                targetDate: targetNewStartLocal.Date,
                startMinutes: targetStartMinutes,
                endMinutes: targetEndMinutes,
                plan: target.Plan,
                status: target.Status,
                user: user,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                requireEndAligned: false,
                cancellationToken: cancellationToken);
            if (!targetPlacement.IsValid)
            {
                var _rTpl = SchedulePlacementValidator.MapPlacementErrorForSwap(targetPlacement);
                var (_mTpl, _oTpl) = TaskHistoryMessages.ForSwap(_rTpl, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oTpl, _mTpl, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rTpl;
            }

            var sourceCheck = await _placementValidator.CheckTimingAndTravelAsync(
                sourceNewStartLocal.Date, sourceStartMinutes, timezoneOffsetMinutes,
                source.TechnicianId, sourceNewStartAt, sourceNewEndAt,
                source.ServiceObject?.AddressId ?? 0, source.Id, cancellationToken);
            if (!sourceCheck.IsValid)
            {
                var _rSc = new TaskSwapResult(
                    sourceCheck.IsInvalidWindow ? TaskSwapError.InvalidSwap : TaskSwapError.TravelInfeasible,
                    sourceCheck.Message,
                    InvalidSwapReason: sourceCheck.IsInvalidWindow ? InvalidSwapReason.PastTime : InvalidSwapReason.None);
                var (_mSc, _oSc) = TaskHistoryMessages.ForSwap(_rSc, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oSc, _mSc, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rSc;
            }

            var targetCheck = await _placementValidator.CheckTimingAndTravelAsync(
                targetNewStartLocal.Date, targetStartMinutes, timezoneOffsetMinutes,
                target.TechnicianId, targetNewStartAt, targetNewEndAt,
                target.ServiceObject?.AddressId ?? 0, target.Id, cancellationToken);
            if (!targetCheck.IsValid)
            {
                var _rTc = new TaskSwapResult(
                    targetCheck.IsInvalidWindow ? TaskSwapError.InvalidSwap : TaskSwapError.TravelInfeasible,
                    targetCheck.Message,
                    InvalidSwapReason: targetCheck.IsInvalidWindow ? InvalidSwapReason.PastTime : InvalidSwapReason.None);
                var (_mTc, _oTc) = TaskHistoryMessages.ForSwap(_rTc, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oTc, _mTc, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rTc;
            }

            source.StartAt = sourceNewStartAt;
            source.EndAt = sourceNewEndAt;
            target.StartAt = targetNewStartAt;
            target.EndAt = targetNewEndAt;

            await using var transaction = await _placementValidator.BeginTransactionIfSupportedAsync(cancellationToken);

            async Task RevertSwapIfNeededAsync()
            {
                source.StartAt = originalSourceStartAt;
                source.EndAt = originalSourceEndAt;
                target.StartAt = originalTargetStartAt;
                target.EndAt = originalTargetEndAt;

                if (transaction == null)
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
            }

            try
            {
                await _db.SaveChangesAsync(cancellationToken);

                var affectedTaskCount = await _db.Tasks
                    .AsNoTracking()
                    .Where(t => t.TechnicianId == source.TechnicianId)
                    .Where(t => t.StartAt >= source.StartAt.Date && t.StartAt < source.StartAt.Date.AddDays(1))
                    .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                    .Where(t => !source.PlanId.HasValue || t.PlanId == source.PlanId)
                    .CountAsync(cancellationToken);
                if (affectedTaskCount > 2)
                {
                    var swapAdjustPlan = await _scheduleRepairService.BuildPlanAsync(
                        new ScheduleRepairRequest
                        {
                            ScenarioKind = ScheduleRepairScenarioKind.SwapAdjust,
                            TechnicianId = source.TechnicianId,
                            DayUtc = source.StartAt.Date,
                            PlanId = source.PlanId,
                            FilterByPlanId = source.PlanId.HasValue,
                            TimezoneOffsetMinutes = timezoneOffsetMinutes,
                            SourceTaskId = source.Id,
                            TargetTaskId = target.Id
                        }, cancellationToken);
                    if (!swapAdjustPlan.Success)
                    {
                        if (transaction != null)
                        {
                            await transaction.RollbackAsync(cancellationToken);
                        }

                        await RevertSwapIfNeededAsync();
                        var _rAp1 = new TaskSwapResult(TaskSwapError.InvalidSwap, swapAdjustPlan.Message ?? "Sukeitimo koregavimas nepavyko.");
                        var (_mAp1, _oAp1) = TaskHistoryMessages.ForSwap(_rAp1, source.Title, target.Title);
                        await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oAp1, _mAp1, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                        return _rAp1;
                    }

                    var swapAdjustApply = await _scheduleRepairService.ApplyPlanAsync(swapAdjustPlan, null, cancellationToken);
                    if (!swapAdjustApply.Success)
                    {
                        if (transaction != null)
                        {
                            await transaction.RollbackAsync(cancellationToken);
                        }

                        await RevertSwapIfNeededAsync();
                        var _rAp2 = new TaskSwapResult(TaskSwapError.InvalidSwap, swapAdjustApply.Message ?? "Sukeitimo koregavimas nepavyko.");
                        var (_mAp2, _oAp2) = TaskHistoryMessages.ForSwap(_rAp2, source.Title, target.Title);
                        await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oAp2, _mAp2, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                        return _rAp2;
                    }
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                var _rDbSt = new TaskSwapResult(
                    TaskSwapError.Stale,
                    "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.",
                    CurrentSourceVersion: sourceCurrentVersion,
                    CurrentTargetVersion: targetCurrentVersion);
                var (_mDbSt, _oDbSt) = TaskHistoryMessages.ForSwap(_rDbSt, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oDbSt, _mDbSt, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rDbSt;
            }

            await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(source.TechnicianId, source.StartAt.Date, "swap", cancellationToken);

            var addressIds = await _db.ServiceObjects
                .AsNoTracking()
                .Where(o => o.Id == source.ServiceObjectId || o.Id == target.ServiceObjectId)
                .ToDictionaryAsync(o => o.Id, o => o.AddressId, cancellationToken);

            var sourceAddressId = addressIds[source.ServiceObjectId];
            var sourceTravelCheck = await _travelFeasibilityService.EvaluatePlacementAsync(
                source.TechnicianId,
                source.StartAt,
                source.EndAt,
                sourceAddressId,
                sourceStartMinutes,
                source.Id,
                checkOverlap: false,
                cancellationToken);
            if (!sourceTravelCheck.IsFeasible)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                var _rStc = new TaskSwapResult(TaskSwapError.TravelInfeasible, sourceTravelCheck.Message ?? "Nepakanka laiko kelionei.");
                var (_mStc, _oStc) = TaskHistoryMessages.ForSwap(_rStc, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oStc, _mStc, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rStc;
            }

            var targetAddressId = addressIds[target.ServiceObjectId];
            var targetTravelCheck = await _travelFeasibilityService.EvaluatePlacementAsync(
                target.TechnicianId,
                target.StartAt,
                target.EndAt,
                targetAddressId,
                targetStartMinutes,
                target.Id,
                checkOverlap: false,
                cancellationToken);
            if (!targetTravelCheck.IsFeasible)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                var _rTtc = new TaskSwapResult(TaskSwapError.TravelInfeasible, targetTravelCheck.Message ?? "Nepakanka laiko kelionei.");
                var (_mTtc, _oTtc) = TaskHistoryMessages.ForSwap(_rTtc, source.Title, target.Title);
                await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oTtc, _mTtc, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rTtc;
            }

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            if (source.PlanId.HasValue && source.Plan != null)
            {
                await _planLifecycle.AdjustPlanSpanAsync(source.Plan, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(source.PlanId.Value, cancellationToken);
            }

            var _rOk = new TaskSwapResult(
                TaskSwapError.None,
                SourceTask: source,
                TargetTask: target,
                CurrentSourceVersion: _db.Entry(source).Property<uint>("xmin").CurrentValue,
                CurrentTargetVersion: _db.Entry(target).Property<uint>("xmin").CurrentValue);
            var (_mOk, _oOk) = TaskHistoryMessages.ForSwap(_rOk, source.Title, target.Title);
            await hw.RecordAsync(sourceTaskId, source.PlanId, "swap", _oOk, _mOk, user.UserId, user.Role.ToString().ToLower(),
                new { sourceTaskId, targetTaskId, sourceNewStartAt = source.StartAt, targetNewStartAt = target.StartAt }, cancellationToken: cancellationToken);
            return _rOk;
        }

        public async Task<TaskSwapResult> SwapTasksSmartAsync(
            long sourceTaskId,
            long targetTaskId,
            uint sourceExpectedVersion,
            uint targetExpectedVersion,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
        {
            // Try strict swap first for the fastest path.
            // Pass recordHistory: false — SwapTasksSmartAsync owns the single log entry.
            var strict = await SwapTasksAsync(
                sourceTaskId,
                targetTaskId,
                sourceExpectedVersion,
                targetExpectedVersion,
                user,
                timezoneOffsetMinutes,
                cancellationToken,
                recordHistory: false);

            if (strict.Ok)
            {
                var (_mSok, _oSok) = TaskHistoryMessages.ForSwap(strict, strict.SourceTask?.Title ?? "", strict.TargetTask?.Title ?? "");
                await _historyWriter.RecordAsync(sourceTaskId, strict.SourceTask?.PlanId, "swap_smart", _oSok, _mSok, user.UserId, user.Role.ToString().ToLower(),
                    new { sourceTaskId, targetTaskId }, cancellationToken: cancellationToken);
                return strict;
            }

            _logger.LogInformation(
                "swap-smart.strict-fail sourceTaskId={SourceTaskId} targetTaskId={TargetTaskId} reason={Reason} message={Message}",
                sourceTaskId,
                targetTaskId,
                strict.Error,
                strict.Message ?? string.Empty);

            // Allow smart-planner fallback for recoverable failures:
            //    TravelInfeasible  adjacent travel times don't fit; planner can reorder.
            //    Overlap         another task is in the way; planner can shift it.
            //   WorkWindow      longer task overflows window at target slot; planner
            //   can pull it back to its earliest feasible slot.
            var isRecoverable =
                strict.Error == TaskSwapError.TravelInfeasible ||
                strict.Error == TaskSwapError.Overlap ||
                (strict.Error == TaskSwapError.InvalidSwap &&
                 strict.InvalidSwapReason == InvalidSwapReason.WorkWindow);

            if (!isRecoverable)
            {
                var (_mNr, _oNr) = TaskHistoryMessages.ForSwap(strict, "", "");
                await _historyWriter.RecordAsync(sourceTaskId, null, "swap_smart", _oNr, _mNr, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return strict;
            }

            // Strict swap may have updated tracked entities before rolling back.
            // Reset tracked state so smart planning/version checks read actual DB values.
            _db.ChangeTracker.Clear();

            var repairPlan = await _placementValidator.BuildSwapRepairPlanAsync(sourceTaskId, targetTaskId, timezoneOffsetMinutes, cancellationToken);

            if (!repairPlan.Success || repairPlan.Adjustments.Count == 0)
            {
                _logger.LogWarning(
                    "swap-smart.plan-fail sourceTaskId={SourceTaskId} targetTaskId={TargetTaskId} reason={Reason}",
                    sourceTaskId,
                    targetTaskId,
                    repairPlan.Message ?? strict.Message ?? string.Empty);
                var _rRpf = new TaskSwapResult(
                    strict.Error,
                    repairPlan.Message ?? strict.Message,
                    CurrentSourceVersion: strict.CurrentSourceVersion,
                    CurrentTargetVersion: strict.CurrentTargetVersion);
                var (_mRpf, _oRpf) = TaskHistoryMessages.ForSwap(_rRpf, string.Empty, string.Empty);
                await _historyWriter.RecordAsync(sourceTaskId, null, "swap_smart", _oRpf, _mRpf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rRpf;
            }

            var source = await _db.Tasks
                .Include(t => t.Plan)
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == sourceTaskId, cancellationToken);
            if (source == null)
            {
                var _rSnf = new TaskSwapResult(TaskSwapError.NotFound, $"Task {sourceTaskId} was not found.");
                var (_mSnf, _oSnf) = TaskHistoryMessages.ForSwap(_rSnf, string.Empty, string.Empty);
                await _historyWriter.RecordAsync(sourceTaskId, null, "swap_smart", _oSnf, _mSnf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rSnf;
            }

            var target = await _db.Tasks
                .Include(t => t.Plan)
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == targetTaskId, cancellationToken);
            if (target == null)
            {
                var _rTnf = new TaskSwapResult(TaskSwapError.TargetNotFound, $"Task {targetTaskId} was not found.");
                var (_mTnf, _oTnf) = TaskHistoryMessages.ForSwap(_rTnf, source.Title, string.Empty);
                await _historyWriter.RecordAsync(sourceTaskId, source.PlanId, "swap_smart", _oTnf, _mTnf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rTnf;
            }

            var sourceCurrentVersion = _db.Entry(source).Property<uint>("xmin").CurrentValue;
            var targetCurrentVersion = _db.Entry(target).Property<uint>("xmin").CurrentValue;
            if (sourceExpectedVersion == 0 || sourceExpectedVersion != sourceCurrentVersion ||
                targetExpectedVersion == 0 || targetExpectedVersion != targetCurrentVersion)
            {
                var _rSt = new TaskSwapResult(
                    TaskSwapError.Stale,
                    "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.",
                    CurrentSourceVersion: sourceCurrentVersion,
                    CurrentTargetVersion: targetCurrentVersion);
                var (_mSt, _oSt) = TaskHistoryMessages.ForSwap(_rSt, source.Title, target.Title);
                await _historyWriter.RecordAsync(sourceTaskId, source.PlanId, "swap_smart", _oSt, _mSt, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _rSt;
            }

                        var affectedIds = repairPlan.Adjustments.Select(x => x.TaskId).Distinct().ToList();

            await using var transaction = await _placementValidator.BeginTransactionIfSupportedAsync(cancellationToken);
            try
            {
                var applyResult = await _scheduleRepairService.ApplyPlanAsync(repairPlan, null, cancellationToken);
                if (!applyResult.Success)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    var _rAf = new TaskSwapResult(TaskSwapError.InvalidSwap, applyResult.Message ?? "Smart swap koregavimo pritaikyti nepavyko.");
                    var (_mAf, _oAf) = TaskHistoryMessages.ForSwap(_rAf, source.Title, target.Title);
                    await _historyWriter.RecordAsync(sourceTaskId, source.PlanId, "swap_smart", _oAf, _mAf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                    return _rAf;
                }

                affectedIds = applyResult.AppliedTaskIds.ToList();
                var affectedTasks = await _db.Tasks
                    .Include(t => t.ServiceObject)
                    .Where(t => affectedIds.Contains(t.Id))
                    .ToListAsync(cancellationToken);
                if (affectedTasks.Count != affectedIds.Count)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    var _rMt = new TaskSwapResult(TaskSwapError.InvalidSwap, "Smart swap koregavimui truksta uzduociu.");
                    var (_mMt, _oMt) = TaskHistoryMessages.ForSwap(_rMt, source.Title, target.Title);
                    await _historyWriter.RecordAsync(sourceTaskId, source.PlanId, "swap_smart", _oMt, _mMt, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                    return _rMt;
                }


                await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(source.TechnicianId, source.StartAt.Date, "swap-smart", cancellationToken);

                foreach (var task in affectedTasks)
                {
                    var addressId = task.ServiceObject.AddressId;
                    var startMinutesLocal = (int)task.StartAt.AddMinutes(timezoneOffsetMinutes).TimeOfDay.TotalMinutes;
                    var travelCheck = await _travelFeasibilityService.EvaluatePlacementAsync(
                        task.TechnicianId,
                        task.StartAt,
                        task.EndAt,
                        addressId,
                        startMinutesLocal,
                        task.Id,
                        checkOverlap: false,
                        cancellationToken);
                    if (!travelCheck.IsFeasible)
                    {
                        if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                        var _rTf = new TaskSwapResult(TaskSwapError.TravelInfeasible, travelCheck.Message ?? "Nepakanka laiko kelionei.");
                        var (_mTf, _oTf) = TaskHistoryMessages.ForSwap(_rTf, source.Title, target.Title);
                        await _historyWriter.RecordAsync(sourceTaskId, source.PlanId, "swap_smart", _oTf, _mTf, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                        return _rTf;
                    }

                    var smartSwapInbound = Math.Max(0, travelCheck.InboundTravelMinutes ?? 0);
                    if (!_timeService.IsTravelStartFeasible(task.StartAt, smartSwapInbound))
                    {
                        if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                        var availableFromNowMinutes = Math.Max(0, (int)Math.Floor((task.StartAt - _timeService.GetCurrentUtcTime()).TotalMinutes));
                        var _rTfs = new TaskSwapResult(
                            TaskSwapError.TravelInfeasible,
                            $"Nepakanka laiko kelionei: kelionei reikia {SchedulePlacementValidator.FormatDurationLabel(smartSwapInbound)}, nuo dabar laisvo laiko yra {SchedulePlacementValidator.FormatDurationLabel(availableFromNowMinutes)}.");
                        var (_mTfs, _oTfs) = TaskHistoryMessages.ForSwap(_rTfs, source.Title, target.Title);
                        await _historyWriter.RecordAsync(sourceTaskId, source.PlanId, "swap_smart", _oTfs, _mTfs, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                        return _rTfs;
                    }
                }

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                if (source.PlanId.HasValue && source.Plan != null)
                {
                    await _planLifecycle.AdjustPlanSpanAsync(source.Plan, cancellationToken);
                    await _evaluationEngine.RecalculatePlanAsync(source.PlanId.Value, cancellationToken);
                }

                var _rOk = new TaskSwapResult(
                    TaskSwapError.None,
                    SourceTask: source,
                    TargetTask: target,
                    CurrentSourceVersion: _db.Entry(source).Property<uint>("xmin").CurrentValue,
                    CurrentTargetVersion: _db.Entry(target).Property<uint>("xmin").CurrentValue);
                var (_mOk, _oOk) = TaskHistoryMessages.ForSwap(_rOk, source.Title, target.Title);
                await _historyWriter.RecordAsync(sourceTaskId, source.PlanId, "swap_smart", _oOk, _mOk, user.UserId, user.Role.ToString().ToLower(),
                    new { sourceTaskId, targetTaskId, adjustedTaskCount = affectedIds.Count }, cancellationToken: cancellationToken);
                return _rOk;
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                throw;
            }
        }

        public async Task<TechnicianUpdateResult> TechnicianUpdateTaskAsync(
            long taskId,
            ScheduleTaskStatus status,
            string? comment,
            uint expectedVersion,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
        {
            // Validate status - technician can only set Completed or Conflict
            if (status != ScheduleTaskStatus.Completed && status != ScheduleTaskStatus.Conflict)
            {
                var _r = new TechnicianUpdateResult(TechnicianUpdateError.InvalidStatus, "Galite nustatyti tik 'Atlikta' arba 'Problema' statusus.");
                var (_msg, _out) = TaskHistoryMessages.ForTechnicianUpdate(_r, status.ToString());
                await _historyWriter.RecordAsync(taskId, null, "technician_update", _out, _msg, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _r;
            }

            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                var _r = new TechnicianUpdateResult(TechnicianUpdateError.TaskNotFound, "Užduotis nerasta.");
                var (_msg, _out) = TaskHistoryMessages.ForTechnicianUpdate(_r, status.ToString());
                await _historyWriter.RecordAsync(taskId, null, "technician_update", _out, _msg, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _r;
            }

            // Single authorization check - TaskPermissionService is the sole authority
            // (handles ownership, time editability, and role-based rules)
            var canEdit = await _taskPermissionService.CanEditTaskAsync(task, user, timezoneOffsetMinutes);
            if (!canEdit)
            {
                // Check if it's an ownership issue for technicians (for specific error message)
                if (user.IsTechnician && (!user.TechnicianId.HasValue || task.TechnicianId != user.TechnicianId.Value))
                {
                    var _r = new TechnicianUpdateResult(TechnicianUpdateError.NotOwner, "Galite redaguoti tik savo užduotis.");
                    var (_msg, _out) = TaskHistoryMessages.ForTechnicianUpdate(_r, status.ToString());
                    await _historyWriter.RecordAsync(taskId, task.PlanId, "technician_update", _out, _msg, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                    return _r;
                }
                var _r2 = new TechnicianUpdateResult(TechnicianUpdateError.NotEditable, "ši uzduotis nebegali buti redaguojama.");
                var (_msg2, _out2) = TaskHistoryMessages.ForTechnicianUpdate(_r2, status.ToString());
                await _historyWriter.RecordAsync(taskId, task.PlanId, "technician_update", _out2, _msg2, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _r2;
            }

            // Check version
            var currentVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            if (currentVersion != expectedVersion)
            {
                var _r = new TechnicianUpdateResult(TechnicianUpdateError.Stale, "Užduotis buvo pakeista kito vartotojo. Prašome atnaujinti puslapį.", CurrentVersion: currentVersion);
                var (_msg, _out) = TaskHistoryMessages.ForTechnicianUpdate(_r, status.ToString());
                await _historyWriter.RecordAsync(taskId, task.PlanId, "technician_update", _out, _msg, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _r;
            }

            // Update task
            task.Status = status;
            task.TechnicianComment = comment ?? string.Empty;

            try
            {await _db.SaveChangesAsync(cancellationToken);

                // Recalculate evaluations when status changes
                if (task.PlanId.HasValue)
                {
                    await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
                }

                var newVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
                var _r = new TechnicianUpdateResult(TechnicianUpdateError.None, Task: task, CurrentVersion: newVersion);
                var (_msg, _out) = TaskHistoryMessages.ForTechnicianUpdate(_r, status.ToString());
                await _historyWriter.RecordAsync(taskId, task.PlanId, "technician_update", _out, _msg, user.UserId, user.Role.ToString().ToLower(),
                    new { newStatus = status.ToString(), hasComment = !string.IsNullOrWhiteSpace(comment) }, cancellationToken: cancellationToken);
                return _r;
            }
            catch (DbUpdateConcurrencyException)
            {
                var _r = new TechnicianUpdateResult(TechnicianUpdateError.Stale, "Užduotis buvo pakeista kito vartotojo. Prašome atnaujinti puslapį.");
                var (_msg, _out) = TaskHistoryMessages.ForTechnicianUpdate(_r, status.ToString());
                await _historyWriter.RecordAsync(taskId, task.PlanId, "technician_update", _out, _msg, user.UserId, user.Role.ToString().ToLower(), cancellationToken: cancellationToken);
                return _r;
            }
        }

        private sealed class NoopTaskHistoryWriter : ITaskHistoryWriter
        {
            public Task RecordAsync(long? taskId, long? planId, string actionType, string outcome, string summary,
                long? actorUserId, string actorRole, object? details = null, string source = "user",
                string? correlationId = null, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }
    }
}
