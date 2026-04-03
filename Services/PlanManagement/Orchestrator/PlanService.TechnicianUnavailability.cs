using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.History;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Unavailability;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator
{
    internal sealed class PlanServiceTechnicianUnavailability
    {
        private sealed record RestoreTaskBuildResult(bool Ok, TaskEntity? Task = null, string? FailureMessage = null);
        private sealed record UnavailabilityTaskDecision(
            TechnicianUnavailabilityImpactResolver.AffectedTaskCandidate Candidate,
            ScheduleRepairPlan? RepairPlan,
            UnavailabilityTaskResolution Resolution);
        private sealed record MutationFollowUp(
            IReadOnlyCollection<long> AffectedPlanIds,
            IReadOnlyCollection<(long TechnicianId, DateTime Day)> AffectedRouteTargets);
        private sealed record MutationResult(
            DisplaceTasksResult Result,
            MutationFollowUp? FollowUp = null);

        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly IScheduleTimeService _timeService;
        private readonly ITaskHistoryWriter _historyWriter;
        private readonly ServiceLocationResolver _locationResolver;
        private readonly RouteEvaluationCaller _routeEvaluationCaller;
        private readonly PlanServicePlanLifecycle _planLifecycle;
        private readonly SchedulePlacementValidator _placementValidator;
        private readonly TechnicianUnavailabilityImpactResolver _impactResolver;
        private readonly ILogger<PlanServiceTechnicianUnavailability> _logger;

        public PlanServiceTechnicianUnavailability(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            IScheduleTimeService timeService,
            ITaskHistoryWriter historyWriter,
            ServiceLocationResolver locationResolver,
            RouteEvaluationCaller routeEvaluationCaller,
            PlanServicePlanLifecycle planLifecycle,
            SchedulePlacementValidator placementValidator,
            TechnicianUnavailabilityImpactResolver impactResolver,
            ILogger<PlanServiceTechnicianUnavailability> logger)
        {
            _db = db;
            _evaluationEngine = evaluationEngine;
            _timeService = timeService;
            _historyWriter = historyWriter;
            _locationResolver = locationResolver;
            _routeEvaluationCaller = routeEvaluationCaller;
            _planLifecycle = planLifecycle;
            _placementValidator = placementValidator;
            _impactResolver = impactResolver;
            _logger = logger;
        }

        public async Task<DisplaceTasksResult> DisplaceTasksToBacklogAsync(
            long technicianId,
            DateTime periodStart,
            DateTime periodEnd,
            UserContext actorUser,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "svc.unavailability.create.request technicianId={TechnicianId} periodStart={Start} periodEnd={End}",
                technicianId,
                periodStart,
                periodEnd);

            if (!actorUser.IsManager)
            {
                return new DisplaceTasksResult(DisplaceTasksError.Unauthorized, Message: UnavailabilityMessages.Unauthorized);
            }

            if (periodEnd <= periodStart)
            {
                return new DisplaceTasksResult(DisplaceTasksError.InvalidPeriod, Message: UnavailabilityMessages.EndNotAfterStart);
            }

            var technicianExists = await _db.Technicians
                .AsNoTracking()
                .AnyAsync(t => t.Id == technicianId, cancellationToken);

            if (!technicianExists)
            {
                return new DisplaceTasksResult(DisplaceTasksError.TechnicianNotFound, Message: UnavailabilityMessages.TechnicianNotFound(technicianId));
            }

            MutationResult operation;

            await using var transaction = await _placementValidator.BeginTransactionIfSupportedAsync(cancellationToken);
            try
            {
                operation = await DisplaceTasksCoreAsync(technicianId, periodStart, periodEnd, actorUser, timezoneOffsetMinutes, cancellationToken);
                if (!operation.Result.Ok)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    return operation.Result;
                }

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                throw;
            }

            await RefreshPlansAndRoutesAsync(
                operation.FollowUp?.AffectedPlanIds ?? Array.Empty<long>(),
                operation.FollowUp?.AffectedRouteTargets ?? Array.Empty<(long TechnicianId, DateTime Day)>(),
                "technician-unavailability-displace",
                cancellationToken);

            return operation.Result;
        }

        public async Task<DisplaceTasksResult> UpdateUnavailabilityAsync(
            long id,
            DateTime newPeriodStart,
            DateTime newPeriodEnd,
            UserContext actorUser,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "svc.unavailability.update.request id={Id} periodStart={Start} periodEnd={End}",
                id,
                newPeriodStart,
                newPeriodEnd);

            if (!actorUser.IsManager)
            {
                return new DisplaceTasksResult(DisplaceTasksError.Unauthorized, Message: UnavailabilityMessages.Unauthorized);
            }

            if (newPeriodEnd <= newPeriodStart)
            {
                return new DisplaceTasksResult(DisplaceTasksError.InvalidPeriod, Message: UnavailabilityMessages.EndNotAfterStart);
            }

            var period = await _db.TechnicianUnavailabilities
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

            if (period == null)
            {
                return new DisplaceTasksResult(DisplaceTasksError.NotFound, Message: UnavailabilityMessages.PeriodNotFound);
            }

            MutationResult restoreOperation;
            MutationResult displaceOperation;

            await using var transaction = await _placementValidator.BeginTransactionIfSupportedAsync(cancellationToken);
            try
            {
                restoreOperation = await RestorePeriodInternalAsync(id, actorUser, timezoneOffsetMinutes, cancellationToken);
                if (!restoreOperation.Result.Ok)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    return restoreOperation.Result;
                }

                displaceOperation = await DisplaceTasksCoreAsync(
                    period.TechnicianId,
                    newPeriodStart,
                    newPeriodEnd,
                    actorUser,
                    timezoneOffsetMinutes,
                    cancellationToken);

                if (!displaceOperation.Result.Ok)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    return displaceOperation.Result;
                }

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                throw;
            }

            await RefreshPlansAndRoutesAsync(
                MergePlanIds(restoreOperation.FollowUp, displaceOperation.FollowUp),
                MergeRouteTargets(restoreOperation.FollowUp, displaceOperation.FollowUp),
                "technician-unavailability-update",
                cancellationToken);

            return new DisplaceTasksResult(
                DisplaceTasksError.None,
                DisplacedCount: displaceOperation.Result.DisplacedCount,
                RepairedCount: displaceOperation.Result.RepairedCount,
                RestoredCount: restoreOperation.Result.RestoredCount,
                BacklogRemainingCount: displaceOperation.Result.BacklogRemainingCount);
        }

        public async Task<DisplaceTasksResult> DeleteUnavailabilityAsync(
            long id,
            UserContext actorUser,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("svc.unavailability.delete.request id={Id}", id);

            if (!actorUser.IsManager)
            {
                return new DisplaceTasksResult(DisplaceTasksError.Unauthorized, Message: UnavailabilityMessages.Unauthorized);
            }

            MutationResult operation;

            await using var transaction = await _placementValidator.BeginTransactionIfSupportedAsync(cancellationToken);
            try
            {
                operation = await RestorePeriodInternalAsync(id, actorUser, timezoneOffsetMinutes, cancellationToken);
                if (!operation.Result.Ok)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    return operation.Result;
                }

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                throw;
            }

            await RefreshPlansAndRoutesAsync(
                operation.FollowUp?.AffectedPlanIds ?? Array.Empty<long>(),
                operation.FollowUp?.AffectedRouteTargets ?? Array.Empty<(long TechnicianId, DateTime Day)>(),
                "technician-unavailability-restore",
                cancellationToken);

            return operation.Result;
        }

        private async Task<MutationResult> DisplaceTasksCoreAsync(
            long technicianId,
            DateTime periodStart,
            DateTime periodEnd,
            UserContext actorUser,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken)
        {
            var decisions = await BuildUnavailabilityTaskDecisionsAsync(
                technicianId,
                periodStart,
                periodEnd,
                timezoneOffsetMinutes,
                cancellationToken);
            var affectedTaskIds = decisions.Select(item => item.Candidate.Task.Id).Distinct().ToList();
            var count = affectedTaskIds.Count;
            var repairedCount = decisions.Count(item => item.Resolution == UnavailabilityTaskResolution.Repair);
            var displacedCount = decisions.Count(item => item.Resolution == UnavailabilityTaskResolution.Backlog);

            _logger.LogInformation(
                "svc.unavailability.displace.affected technicianId={TechnicianId} periodStart={Start} periodEnd={End} affectedTaskIds=[{TaskIds}]",
                technicianId,
                periodStart,
                periodEnd,
                string.Join(",", affectedTaskIds));

            var now = DateTime.UtcNow;
            var unavailability = new TechnicianUnavailabilityEntity
            {
                TechnicianId = technicianId,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd,
                CreatedAt = now
            };

            _db.TechnicianUnavailabilities.Add(unavailability);
            await _db.SaveChangesAsync(cancellationToken);

            if (count == 0)
            {
                await _historyWriter.RecordAsync(
                    taskId: null,
                    planId: null,
                    actionType: "technician_unavailability_displace",
                    outcome: "success",
                    summary: $"Perkelta i eile: 0 uzduociu (technikas {technicianId}, {periodStart:yyyy-MM-dd HH:mm}-{periodEnd:yyyy-MM-dd HH:mm}).",
                    actorUserId: actorUser.UserId,
                    actorRole: "manager",
                    details: new { technicianId, periodStart, periodEnd, displacedCount = 0, repairedCount = 0, unavailabilityId = unavailability.Id },
                    cancellationToken: cancellationToken);

                return new MutationResult(new DisplaceTasksResult(DisplaceTasksError.None, DisplacedCount: 0, RepairedCount: 0));
            }

            var trackedTasks = await _db.Tasks
                .Include(task => task.Plan)
                .Include(task => task.ServiceObject)
                    .ThenInclude(serviceObject => serviceObject.Address)
                .Where(task => affectedTaskIds.Contains(task.Id))
                .ToListAsync(cancellationToken);

            if (trackedTasks.Count != count)
            {
                return new MutationResult(new DisplaceTasksResult(
                    DisplaceTasksError.Stale,
                    Message: UnavailabilityMessages.StaleTasksChanged));
            }

            var trackedTaskById = trackedTasks.ToDictionary(task => task.Id);

            var affectedPlanIds = trackedTasks
                .Where(task => task.PlanId.HasValue)
                .Select(task => task.PlanId!.Value)
                .Distinct()
                .ToArray();

            var affectedRouteTargets = trackedTasks
                .Select(task => (task.TechnicianId, task.StartAt.Date))
                .Distinct()
                .ToArray();

            var backlogItems = new List<BacklogItemEntity>();
            var backloggedTasks = new List<TaskEntity>();
            var repairedTasks = new List<TaskEntity>();

            foreach (var decision in decisions.OrderBy(item => item.Candidate.Task.StartAt))
            {
                var task = trackedTaskById[decision.Candidate.Task.Id];
                if (decision.Resolution == UnavailabilityTaskResolution.Repair)
                {
                    // Snapshot pre-repair position so RestorePeriodInternalAsync can move
                    // the task back when the unavailability is deleted.
                    task.SourceUnavailabilityId = unavailability.Id;
                    task.OriginalStartAt        = task.StartAt;
                    task.OriginalEndAt          = task.EndAt;

                    var validation = new SchedulePlacementValidator.PlacementValidationResult(
                        SchedulePlacementValidator.PlacementError.None,
                        string.Empty,
                        task.StartAt,
                        task.EndAt,
                        string.Empty,
                        decision.RepairPlan,
                        false);

                    var repairApplied = await _placementValidator.TryApplyPlacementRepairAsync(task, validation, cancellationToken);
                    if (!repairApplied)
                    {
                        return new MutationResult(new DisplaceTasksResult(
                            DisplaceTasksError.Stale,
                            Message: UnavailabilityMessages.StaleTasksChanged));
                    }

                    repairedTasks.Add(task);
                    continue;
                }

                backlogItems.Add(new BacklogItemEntity
                {
                    CreatedByUserId = actorUser.UserId!.Value,
                    Title = task.Title,
                    Priority = task.Priority,
                    ItemType = BacklogItemType.Temporary,
                    SkillLevel = task.SkillLevel,
                    ObjectName = task.ServiceObject.Name,
                    ObjectAddress = task.ServiceObject.Address.FullAddress,
                    Instructions = task.Instructions,
                    DueDate = task.StartAt.Date,
                    SourceTaskId = task.Id,
                    SourceUnavailabilityId = unavailability.Id,
                    OriginalTechnicianId = task.TechnicianId,
                    OriginalStartAt = task.StartAt,
                    OriginalEndAt = task.EndAt,
                    CreatedAt = now
                });
                backloggedTasks.Add(task);
            }

            _db.BacklogItems.AddRange(backlogItems);
            _db.Tasks.RemoveRange(backloggedTasks);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new MutationResult(new DisplaceTasksResult(
                    DisplaceTasksError.Stale,
                    Message: UnavailabilityMessages.StaleTasksChanged));
            }

            foreach (var task in repairedTasks)
            {
                await _historyWriter.RecordAsync(
                    taskId: task.Id,
                    planId: task.PlanId,
                    actionType: "technician_unavailability_repair",
                    outcome: "success",
                    summary: $"Uzduotis perkelta i velesni laika del techniko neaktyvumo ({periodStart:yyyy-MM-dd HH:mm}-{periodEnd:yyyy-MM-dd HH:mm}).",
                    actorUserId: actorUser.UserId,
                    actorRole: "manager",
                    details: new { taskId = task.Id, technicianId, unavailabilityId = unavailability.Id, resolution = "repair" },
                    cancellationToken: cancellationToken);
            }

            foreach (var task in backloggedTasks)
            {
                await _historyWriter.RecordAsync(
                    taskId: task.Id,
                    planId: task.PlanId,
                    actionType: "technician_unavailability_displace",
                    outcome: "success",
                    summary: $"Uzduotis perkelta i eile del techniko neaktyvumo ({periodStart:yyyy-MM-dd HH:mm}-{periodEnd:yyyy-MM-dd HH:mm}).",
                    actorUserId: actorUser.UserId,
                    actorRole: "manager",
                    details: new { taskId = task.Id, technicianId, unavailabilityId = unavailability.Id },
                    cancellationToken: cancellationToken);
            }

            await _historyWriter.RecordAsync(
                taskId: null,
                planId: null,
                actionType: "technician_unavailability_displace",
                outcome: "success",
                summary: $"Neaktyvumas pritaikytas: perkelta i eile {displacedCount}, perplanuota {repairedCount} (technikas {technicianId}, {periodStart:yyyy-MM-dd HH:mm}-{periodEnd:yyyy-MM-dd HH:mm}).",
                actorUserId: actorUser.UserId,
                actorRole: "manager",
                details: new { technicianId, periodStart, periodEnd, displacedCount, repairedCount, unavailabilityId = unavailability.Id },
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "svc.unavailability.displace technicianId={TechnicianId} periodStart={Start} periodEnd={End} displaced={DisplacedCount} repaired={RepairedCount}",
                technicianId,
                periodStart,
                periodEnd,
                displacedCount,
                repairedCount);

            return new MutationResult(
                new DisplaceTasksResult(DisplaceTasksError.None, DisplacedCount: displacedCount, RepairedCount: repairedCount),
                new MutationFollowUp(affectedPlanIds, affectedRouteTargets));
        }

        private async Task<List<UnavailabilityTaskDecision>> BuildUnavailabilityTaskDecisionsAsync(
            long technicianId,
            DateTime periodStart,
            DateTime periodEnd,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken)
        {
            var affectedTasks = await _impactResolver.GetAffectedTasksAsync(
                technicianId,
                periodStart,
                periodEnd,
                timezoneOffsetMinutes,
                cancellationToken);

            var decisions = new List<UnavailabilityTaskDecision>(affectedTasks.Count);
            foreach (var affectedTask in affectedTasks.OrderBy(item => item.Task.StartAt))
            {
                var serviceAddressId = affectedTask.Task.ServiceObject?.AddressId;
                if (!serviceAddressId.HasValue)
                {
                    decisions.Add(new UnavailabilityTaskDecision(
                        affectedTask,
                        RepairPlan: null,
                        Resolution: UnavailabilityTaskResolution.Backlog));
                    continue;
                }

                var repairPlan = await _placementValidator.TryBuildUnavailabilityRepairPlanAsync(
                    technicianId,
                    affectedTask.Task.StartAt,
                    affectedTask.Task.EndAt,
                    affectedTask.EffectiveStartAt,
                    periodEnd,
                    serviceAddressId.Value,
                    timezoneOffsetMinutes,
                    affectedTask.Task.Id,
                    cancellationToken,
                    blockingPeriodStartUtc: periodStart);

                decisions.Add(new UnavailabilityTaskDecision(
                    affectedTask,
                    RepairPlan: repairPlan.Success ? repairPlan : null,
                    Resolution: repairPlan.Success
                        ? UnavailabilityTaskResolution.Repair
                        : UnavailabilityTaskResolution.Backlog));
            }

            return decisions;
        }

        private async Task<MutationResult> RestorePeriodInternalAsync(
            long id,
            UserContext actorUser,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken)
        {
            var period = await _db.TechnicianUnavailabilities
                .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

            if (period == null)
            {
                return new MutationResult(new DisplaceTasksResult(DisplaceTasksError.NotFound, Message: UnavailabilityMessages.PeriodNotFound));
            }

            var backlogItems = await _db.BacklogItems
                .Where(item => item.SourceUnavailabilityId == id)
                .OrderBy(item => item.OriginalStartAt)
                .ToListAsync(cancellationToken);

            // Repaired tasks: still exist at their pushed-forward position but hold
            // a snapshot of the original slot. Load before deleting the period so
            // we have them in hand; restore after deletion so the slot is unblocked.
            var repairedTasks = await _db.Tasks
                .Where(t => t.SourceUnavailabilityId == id)
                .ToListAsync(cancellationToken);

            _db.TechnicianUnavailabilities.Remove(period);
            await _db.SaveChangesAsync(cancellationToken);

            // Attempt to move each repaired task back to its original slot.
            // If the slot is now occupied by another task, leave the task at its
            // repaired position (it is still validly scheduled) and clear the snapshot.
            var movedBackCount = 0;
            var repairedAffectedPlanIds = new List<long>();
            var repairedAffectedRouteTargets = new List<(long TechnicianId, DateTime Day)>();

            foreach (var repairedTask in repairedTasks.OrderBy(t => t.OriginalStartAt))
            {
                if (!repairedTask.OriginalStartAt.HasValue || !repairedTask.OriginalEndAt.HasValue)
                {
                    repairedTask.SourceUnavailabilityId = null;
                    repairedTask.OriginalStartAt        = null;
                    repairedTask.OriginalEndAt          = null;
                    continue;
                }

                var origStartLocal = _timeService.ConvertUtcToLocal(repairedTask.OriginalStartAt.Value, timezoneOffsetMinutes);
                var origEndLocal   = _timeService.ConvertUtcToLocal(repairedTask.OriginalEndAt.Value,   timezoneOffsetMinutes);
                var targetPlan     = await ResolveTargetPlanAsync(origStartLocal.Date, timezoneOffsetMinutes, cancellationToken);

                var placementValidation = await _placementValidator.ValidatePlacementForActionAsync(
                    ignoreTaskIds: [repairedTask.Id],
                    targetTechnicianId: repairedTask.TechnicianId,
                    targetDate: origStartLocal.Date,
                    startMinutes: (int)origStartLocal.TimeOfDay.TotalMinutes,
                    endMinutes: (int)origEndLocal.TimeOfDay.TotalMinutes,
                    plan: targetPlan,
                    status: ScheduleTaskStatus.InProgress,
                    user: actorUser,
                    timezoneOffsetMinutes: timezoneOffsetMinutes,
                    requireEndAligned: true,
                    cancellationToken: cancellationToken);

                if (placementValidation.IsValid)
                {
                    if (repairedTask.PlanId.HasValue) repairedAffectedPlanIds.Add(repairedTask.PlanId.Value);
                    repairedAffectedRouteTargets.Add((repairedTask.TechnicianId, repairedTask.StartAt.Date));
                    repairedAffectedRouteTargets.Add((repairedTask.TechnicianId, origStartLocal.Date));

                    repairedTask.StartAt = placementValidation.StartAtUtc;
                    repairedTask.EndAt   = placementValidation.EndAtUtc;
                    movedBackCount++;
                }
                else
                {
                    _logger.LogInformation(
                        "svc.unavailability.restore.repair.kept-at-repaired-slot unavailabilityId={UnavailabilityId} taskId={TaskId} reason={Reason}",
                        id,
                        repairedTask.Id,
                        placementValidation.Message);
                }

                repairedTask.SourceUnavailabilityId = null;
                repairedTask.OriginalStartAt        = null;
                repairedTask.OriginalEndAt          = null;
            }

            var restoredTasks = new List<TaskEntity>();
            foreach (var backlogItem in backlogItems)
            {
                _logger.LogInformation(
                    "svc.unavailability.restore.item.start unavailabilityId={UnavailabilityId} backlogItemId={BacklogItemId} title={Title} technicianId={TechnicianId} originalStartAt={OriginalStartAt} originalEndAt={OriginalEndAt}",
                    id,
                    backlogItem.Id,
                    backlogItem.Title,
                    backlogItem.OriginalTechnicianId,
                    backlogItem.OriginalStartAt,
                    backlogItem.OriginalEndAt);

                var restoreTaskResult = await BuildRestoredTaskAsync(backlogItem, actorUser, timezoneOffsetMinutes, cancellationToken);
                if (!restoreTaskResult.Ok || restoreTaskResult.Task == null)
                {
                    var failureMessage = restoreTaskResult.FailureMessage ?? UnavailabilityMessages.RestoreItemFailed;
                    _logger.LogWarning(
                        "svc.unavailability.restore.item.failed unavailabilityId={UnavailabilityId} backlogItemId={BacklogItemId} title={Title} reason={Reason}",
                        id,
                        backlogItem.Id,
                        backlogItem.Title,
                        failureMessage);

                    return new MutationResult(
                        new DisplaceTasksResult(
                            DisplaceTasksError.RestoreFailed,
                            Message: failureMessage));
                }

                restoredTasks.Add(restoreTaskResult.Task);
                _db.Tasks.Add(restoreTaskResult.Task);
            }

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new MutationResult(new DisplaceTasksResult(
                    DisplaceTasksError.Stale,
                    Message: UnavailabilityMessages.StaleDataChanged));
            }

            foreach (var restoredTask in restoredTasks)
            {
                var addressId = await _db.ServiceObjects
                    .Where(o => o.Id == restoredTask.ServiceObjectId)
                    .Select(o => o.AddressId)
                    .FirstAsync(cancellationToken);

                var localStart = _timeService.ConvertUtcToLocal(restoredTask.StartAt, timezoneOffsetMinutes);
                var timingTravelValidation = await _placementValidator.ValidateTimingAndTravelAsync(
                    taskDateLocal: localStart.Date,
                    startMinutesLocal: (int)localStart.TimeOfDay.TotalMinutes,
                    timezoneOffsetMinutes: timezoneOffsetMinutes,
                    technicianId: restoredTask.TechnicianId,
                    startAtUtc: restoredTask.StartAt,
                    endAtUtc: restoredTask.EndAt,
                    serviceAddressId: addressId,
                    excludeTaskId: restoredTask.Id,
                    cancellationToken: cancellationToken);

                if (!timingTravelValidation.IsValid)
                {
                    var failureMessage = timingTravelValidation.Message ?? "Nepavyko atkurti uzduoties i pradine laika.";
                    _logger.LogWarning(
                        "svc.unavailability.restore.item.travel-invalid unavailabilityId={UnavailabilityId} taskId={TaskId} title={Title} reason={Reason}",
                        id,
                        restoredTask.Id,
                        restoredTask.Title,
                        failureMessage);

                    return new MutationResult(
                        new DisplaceTasksResult(
                            DisplaceTasksError.RestoreFailed,
                            Message: failureMessage));
                }
            }

            _db.BacklogItems.RemoveRange(backlogItems);
            await _db.SaveChangesAsync(cancellationToken);

            var affectedPlanIds = restoredTasks
                .Where(task => task.PlanId.HasValue)
                .Select(task => task.PlanId!.Value)
                .Distinct()
                .ToArray();

            var affectedRouteTargets = restoredTasks
                .Select(task => (task.TechnicianId, task.StartAt.Date))
                .Distinct()
                .ToArray();

            foreach (var restoredTask in restoredTasks)
            {
                await _historyWriter.RecordAsync(
                    taskId: restoredTask.Id,
                    planId: restoredTask.PlanId,
                    actionType: "technician_unavailability_restore",
                    outcome: "success",
                    summary: "Uzduotis atkurta is eiles po techniko neaktyvumo pasalinimo.",
                    actorUserId: actorUser.UserId,
                    actorRole: "manager",
                    details: new { taskId = restoredTask.Id, technicianId = restoredTask.TechnicianId, sourceUnavailabilityId = id },
                    cancellationToken: cancellationToken);
            }

            var totalRestoredCount = restoredTasks.Count + movedBackCount;

            await _historyWriter.RecordAsync(
                taskId: null,
                planId: null,
                actionType: "technician_unavailability_restore",
                outcome: "success",
                summary: $"Atkurta uzduociu: {restoredTasks.Count} is eiles, {movedBackCount} grazinta i pradini laika (periodas {id}).",
                actorUserId: actorUser.UserId,
                actorRole: "manager",
                details: new { unavailabilityId = id, restoredFromBacklog = restoredTasks.Count, movedBackCount },
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "svc.unavailability.restore id={Id} technicianId={TechnicianId} restoredFromBacklog={RestoredCount} movedBack={MovedBackCount}",
                id,
                period.TechnicianId,
                restoredTasks.Count,
                movedBackCount);

            var allAffectedPlanIds = affectedPlanIds
                .Concat(repairedAffectedPlanIds)
                .Distinct()
                .ToArray();
            var allAffectedRouteTargets = affectedRouteTargets
                .Concat(repairedAffectedRouteTargets)
                .Distinct()
                .ToArray();

            return new MutationResult(
                new DisplaceTasksResult(
                    DisplaceTasksError.None,
                    RestoredCount: totalRestoredCount,
                    BacklogRemainingCount: 0),
                new MutationFollowUp(allAffectedPlanIds, allAffectedRouteTargets));
        }

        private async Task<RestoreTaskBuildResult> BuildRestoredTaskAsync(
            BacklogItemEntity backlogItem,
            UserContext actorUser,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken)
        {
            if (!backlogItem.OriginalTechnicianId.HasValue ||
                !backlogItem.OriginalStartAt.HasValue ||
                !backlogItem.OriginalEndAt.HasValue)
            {
                return new RestoreTaskBuildResult(
                    false,
                    FailureMessage: UnavailabilityMessages.RestoreItemMissingData(backlogItem.Title));
            }

            var originalStartLocal = _timeService.ConvertUtcToLocal(backlogItem.OriginalStartAt.Value, timezoneOffsetMinutes);
            var originalEndLocal = _timeService.ConvertUtcToLocal(backlogItem.OriginalEndAt.Value, timezoneOffsetMinutes);
            var targetPlan = await ResolveTargetPlanAsync(originalStartLocal.Date, timezoneOffsetMinutes, cancellationToken);

            var placementValidation = await _placementValidator.ValidatePlacementForActionAsync(
                ignoreTaskIds: null,
                targetTechnicianId: backlogItem.OriginalTechnicianId.Value,
                targetDate: originalStartLocal.Date,
                startMinutes: (int)originalStartLocal.TimeOfDay.TotalMinutes,
                endMinutes: (int)originalEndLocal.TimeOfDay.TotalMinutes,
                plan: targetPlan,
                status: ScheduleTaskStatus.InProgress,
                user: actorUser,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                requireEndAligned: true,
                cancellationToken: cancellationToken);

            if (!placementValidation.IsValid)
            {
                return new RestoreTaskBuildResult(
                    false,
                    FailureMessage: UnavailabilityMessages.RestoreItemPlacementFailed(backlogItem.Title, placementValidation.Message));
            }

            var serviceObjectId = await _locationResolver.FindOrCreateServiceObjectAsync(
                backlogItem.ObjectName,
                backlogItem.ObjectAddress,
                placementValidation.City,
                cancellationToken);

            return new RestoreTaskBuildResult(
                true,
                new TaskEntity
                {
                    PlanId = targetPlan.Id,
                    TechnicianId = backlogItem.OriginalTechnicianId.Value,
                    ServiceObjectId = serviceObjectId,
                    Title = backlogItem.Title,
                    StartAt = placementValidation.StartAtUtc,
                    EndAt = placementValidation.EndAtUtc,
                    Priority = backlogItem.Priority,
                    Status = ScheduleTaskStatus.InProgress,
                    SkillLevel = backlogItem.SkillLevel,
                    Instructions = backlogItem.Instructions,
                    SourceBacklogItemId = backlogItem.Id,
                    CreatedAt = DateTime.UtcNow
                });
        }

        private async Task<PlanEntity> ResolveTargetPlanAsync(DateTime taskDate, int timezoneOffsetMinutes, CancellationToken cancellationToken)
        {
            var userLocalToday = _timeService.GetUserLocalToday(timezoneOffsetMinutes);
            var draftPlan = await _db.Plans.FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);
            if (draftPlan != null && taskDate >= draftPlan.PeriodStart.Date && taskDate <= draftPlan.PeriodEnd.Date)
            {
                return draftPlan;
            }

            var activePlan = await _db.Plans.FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);
            if (activePlan != null && taskDate >= activePlan.PeriodStart.Date && taskDate <= activePlan.PeriodEnd.Date)
            {
                return activePlan;
            }

            var supersededPlan = await _db.Plans
                .Where(p => p.Status == PlanStatus.Superseded &&
                            taskDate >= p.PeriodStart.Date &&
                            taskDate <= p.PeriodEnd.Date)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (supersededPlan != null)
            {
                return supersededPlan;
            }

            return await _planLifecycle.GetOrExtendActivePlanAsync(activePlan, taskDate, userLocalToday, cancellationToken);
        }

        private async Task RefreshPlansAndRoutesAsync(
            IReadOnlyCollection<long> affectedPlanIds,
            IReadOnlyCollection<(long TechnicianId, DateTime Day)> affectedRouteTargets,
            string action,
            CancellationToken cancellationToken)
        {
            foreach (var planId in affectedPlanIds.Distinct())
            {
                var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);
                if (plan != null)
                {
                    await _planLifecycle.AdjustPlanSpanAsync(plan, cancellationToken);
                }
            }

            await _routeEvaluationCaller.TriggerRouteEvaluationBatchSafelyAsync(
                affectedRouteTargets.Distinct().ToArray(),
                action,
                recalculateEvaluations: false,
                cancellationToken);

            foreach (var planId in affectedPlanIds.Distinct())
            {
                await _evaluationEngine.RecalculatePlanAsync(planId, cancellationToken);
            }
        }

        private static long[] MergePlanIds(params MutationFollowUp?[] followUps) =>
            followUps
                .Where(followUp => followUp != null)
                .SelectMany(followUp => followUp!.AffectedPlanIds)
                .Distinct()
                .ToArray();

        private static (long TechnicianId, DateTime Day)[] MergeRouteTargets(params MutationFollowUp?[] followUps) =>
            followUps
                .Where(followUp => followUp != null)
                .SelectMany(followUp => followUp!.AffectedRouteTargets)
                .Distinct()
                .ToArray();
    }
}
