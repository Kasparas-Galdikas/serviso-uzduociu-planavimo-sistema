using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.History;
using ServiceTaskPlanningSystem.Services.PlanManagement.Matching;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public class PlanService : IPlanService
    {
        // ── Raw injected dependencies (kept for DI compatibility) ────────────
        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly IPlanningCalendar _planningCalendar;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly IScheduleTimeService _timeService;
        private readonly IRouteEvaluationService _routeEvaluationService;
        private readonly ITaskTravelFeasibilityService _travelFeasibilityService;
        private readonly IScheduleRepairService _scheduleRepairService;
        private readonly ITravelTimeResolver _travelResolver;
        private readonly ITaskHistoryWriter _historyWriter;
        private readonly ILogger<PlanService> _logger;

        // ── Orchestrator sub-services ────────────────────────────────────────
        private readonly ServiceLocationResolver _locationResolver;
        private readonly RouteEvaluationCaller _routeEvaluationCaller;
        private readonly SchedulePlacementValidator _placementValidator;
        private readonly PlanServicePlanLifecycle _planLifecycle;
        private readonly PlanServiceTaskQuery _taskQuery;
        private readonly PlanServiceTaskCreation _taskCreation;
        private readonly PlanServiceTaskModification _taskModification;
        private readonly PlanServiceTaskDeletion _taskDeletion;
        private readonly PlanServiceTaskPreview _taskPreview;
        private readonly PlanServiceAssignmentMatching _assignmentMatching;

        // ── Noop fallbacks for optional dependencies ─────────────────────────

        private sealed class NoopScheduleRepairService : IScheduleRepairService
        {
            public Task<ScheduleRepairPlan> BuildPlanAsync(
                ScheduleRepairRequest request,
                CancellationToken cancellationToken = default)
                => Task.FromResult(ScheduleRepairPlan.Fail("Planavimo taisymo servisas nesukonfiguruotas."));

            public Task<ScheduleRepairApplyResult> ApplyPlanAsync(
                ScheduleRepairPlan plan,
                long? primaryTaskId,
                CancellationToken cancellationToken = default)
                => Task.FromResult(ScheduleRepairApplyResult.Fail("Planavimo taisymo servisas nesukonfiguruotas."));
        }

        private sealed class NoopTaskHistoryWriter : ITaskHistoryWriter
        {
            public Task RecordAsync(
                long? taskId, long? planId, string actionType, string outcome, string summary,
                long? actorUserId, string actorRole, object? details = null, string source = "user",
                string? correlationId = null, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }

        private sealed class ForwardingLogger<TCategoryName> : ILogger<TCategoryName>
        {
            private readonly ILogger _inner;

            public ForwardingLogger(ILogger inner)
            {
                _inner = inner;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
                => _inner.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel)
                => _inner.IsEnabled(logLevel);

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _inner.Log(logLevel, eventId, state, exception, formatter);
        }

        // ── Constructors ─────────────────────────────────────────────────────

        public PlanService(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            IPlanningCalendar planningCalendar,
            ITaskPermissionService taskPermissionService,
            IScheduleTimeService timeService,
            IRouteEvaluationService routeEvaluationService,
            ITaskTravelFeasibilityService travelFeasibilityService,
            ITravelTimeResolver travelResolver,
            ILogger<PlanService> logger)
            : this(db, evaluationEngine, planningCalendar, taskPermissionService,
                   timeService, routeEvaluationService, travelFeasibilityService,
                   new NoopScheduleRepairService(), travelResolver,
                   new NoopTaskHistoryWriter(), logger)
        { }

        public PlanService(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            IPlanningCalendar planningCalendar,
            ITaskPermissionService taskPermissionService,
            IScheduleTimeService timeService,
            IRouteEvaluationService routeEvaluationService,
            ITaskTravelFeasibilityService travelFeasibilityService,
            IScheduleRepairService scheduleRepairService,
            ITravelTimeResolver travelResolver,
            ITaskHistoryWriter historyWriter,
            ILogger<PlanService> logger)
        {
            _db                      = db;
            _evaluationEngine        = evaluationEngine;
            _planningCalendar        = planningCalendar;
            _taskPermissionService   = taskPermissionService;
            _timeService             = timeService;
            _routeEvaluationService  = routeEvaluationService;
            _travelFeasibilityService = travelFeasibilityService;
            _scheduleRepairService   = scheduleRepairService;
            _travelResolver          = travelResolver;
            _historyWriter           = historyWriter;
            _logger                  = logger;

            _locationResolver = new ServiceLocationResolver(db);

            _routeEvaluationCaller = new RouteEvaluationCaller(
                routeEvaluationService,
                new ForwardingLogger<RouteEvaluationCaller>(logger));

            _placementValidator = new SchedulePlacementValidator(
                db, taskPermissionService, timeService,
                travelFeasibilityService, scheduleRepairService,
                new ForwardingLogger<SchedulePlacementValidator>(logger));

            _planLifecycle = new PlanServicePlanLifecycle(
                db, evaluationEngine, planningCalendar, timeService,
                new ForwardingLogger<PlanServicePlanLifecycle>(logger));

            _taskQuery = new PlanServiceTaskQuery(db, timeService, travelResolver, _placementValidator);

            _assignmentMatching = new PlanServiceAssignmentMatching(
                db,
                timeService,
                _placementValidator,
                travelResolver,
                new CandidateEligibilityEvaluator(),
                new CandidateLocalitySelector(),
                new CandidateDateWindowGenerator(),
                new CandidateSlotGenerator(timeService),
                new CandidateScoreCalculator(),
                new CandidateRankingService(),
                new ForwardingLogger<PlanServiceAssignmentMatching>(logger));

            _taskCreation = new PlanServiceTaskCreation(
                db, evaluationEngine, timeService, scheduleRepairService, historyWriter,
                _placementValidator, _locationResolver, _routeEvaluationCaller, _planLifecycle, _assignmentMatching,
                new ForwardingLogger<PlanServiceTaskCreation>(logger));

            _taskModification = new PlanServiceTaskModification(
                db, evaluationEngine, timeService, scheduleRepairService,
                taskPermissionService, historyWriter,
                _placementValidator, _locationResolver, _routeEvaluationCaller, _planLifecycle,
                travelFeasibilityService,
                new ForwardingLogger<PlanServiceTaskModification>(logger));

            _taskDeletion = new PlanServiceTaskDeletion(
                db, evaluationEngine, scheduleRepairService, taskPermissionService, historyWriter,
                _routeEvaluationCaller, _planLifecycle,
                new ForwardingLogger<PlanServiceTaskDeletion>(logger));

            _taskPreview = new PlanServiceTaskPreview(
                db, timeService, taskPermissionService, travelResolver,
                _placementValidator, routeEvaluationService,
                new ForwardingLogger<PlanServiceTaskPreview>(logger));
        }

        // ── Plan lifecycle ───────────────────────────────────────────────────

        public Task<PlanEntity?> GetActivePlanAsync(CancellationToken cancellationToken = default)
            => _planLifecycle.GetActivePlanAsync(cancellationToken);

        public Task<PlanEntity?> GetDraftPlanAsync(CancellationToken cancellationToken = default)
            => _planLifecycle.GetDraftPlanAsync(cancellationToken);

        public Task<PlanEntity> CreateDraftFromActiveAsync(
            DateTime? requestedStart = null,
            DateTime? requestedEnd = null,
            int? requestedWeeks = null,
            CancellationToken cancellationToken = default)
            => _planLifecycle.CreateDraftFromActiveAsync(requestedStart, requestedEnd, requestedWeeks, cancellationToken);

        public Task PublishDraftAsync(CancellationToken cancellationToken = default)
            => _planLifecycle.PublishDraftAsync(cancellationToken);

        public Task DiscardDraftAsync(CancellationToken cancellationToken = default)
            => _planLifecycle.DiscardDraftAsync(cancellationToken);

        public Task<(long? ActivePlanId, long? DraftPlanId)> RecalculateCurrentPlansAsync(CancellationToken cancellationToken = default)
            => _planLifecycle.RecalculateCurrentPlansAsync(cancellationToken);

        // ── Task queries ─────────────────────────────────────────────────────

        public Task<LoadSnapshotDto> GetWorkloadSnapshotAsync(
            long technicianId,
            DateTime dayStartUtc,
            CancellationToken cancellationToken = default)
            => _taskQuery.GetWorkloadSnapshotAsync(technicianId, dayStartUtc, cancellationToken);

        public Task<TaskDetailResult?> GetTaskDetailAsync(
            long taskId,
            CancellationToken cancellationToken = default)
            => _taskQuery.GetTaskDetailAsync(taskId, cancellationToken);

        public Task<uint> GetTaskVersionAsync(
            long taskId,
            CancellationToken cancellationToken = default)
            => _taskQuery.GetTaskVersionAsync(taskId, cancellationToken);

        public Task<bool> TechnicianExistsAsync(
            long technicianId,
            CancellationToken cancellationToken = default)
            => _taskQuery.TechnicianExistsAsync(technicianId, cancellationToken);

        public Task<bool> HasTaskOverlapAsync(
            long excludeTaskId,
            long technicianId,
            DateTime startUtc,
            DateTime endUtc,
            CancellationToken cancellationToken = default)
            => _taskQuery.HasTaskOverlapAsync(excludeTaskId, technicianId, startUtc, endUtc, cancellationToken);

        public Task<(DateTime TargetDate, int StartMinutes, int EndMinutes)> GetSuggestedNextSlotAsync(
            long technicianId,
            DateTime targetDate,
            int timezoneOffsetMinutes,
            int slotSizeMinutes,
            int dayStartMinutes,
            int? afterEndMinutes,
            long? previousServiceObjectId = null,
            long? newServiceObjectId = null,
            bool appendAfterLastTask = false,
            List<SimulatedTaskAssignmentDto>? simulatedAssignments = null,
            CancellationToken cancellationToken = default)
            => _taskQuery.GetSuggestedNextSlotAsync(
                technicianId, targetDate, timezoneOffsetMinutes,
                slotSizeMinutes, dayStartMinutes, afterEndMinutes,
                previousServiceObjectId, newServiceObjectId, appendAfterLastTask, simulatedAssignments, cancellationToken);

        // ── Task creation ────────────────────────────────────────────────────

        public Task<TaskCreateResult> CreateTaskAsync(
            long technicianId,
            DateTime targetDate,
            int startMinutes,
            int endMinutes,
            string title,
            TaskPriority priority,
            ScheduleTaskStatus status,
            string skillLevel,
            string objectName,
            string objectAddress,
            string instructions,
            UserContext user,
            int timezoneOffsetMinutes,
            long? sourceBacklogItemId = null,
            CancellationToken cancellationToken = default)
            => _taskCreation.CreateTaskAsync(
                technicianId, targetDate, startMinutes, endMinutes, title,
                priority, status, skillLevel, objectName, objectAddress, instructions,
                user, timezoneOffsetMinutes, sourceBacklogItemId, cancellationToken);

        public Task<BulkCreateTaskResponseDto> BulkValidateTasksAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            string? planMode,
            UserContext user,
            CancellationToken cancellationToken = default)
            => _taskCreation.BulkValidateTasksAsync(items, planMode, user, cancellationToken);

        public Task<BulkCreateTaskResponseDto> BulkCreateTasksAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            string? planMode,
            UserContext user,
            CancellationToken cancellationToken = default)
            => _taskCreation.BulkCreateTasksAsync(items, planMode, user, cancellationToken);

        // ── Task modification ────────────────────────────────────────────────

        public Task<TaskUpdateResult> UpdateTaskAsync(
            long taskId,
            Action<TaskEntity> applyChanges,
            long technicianId,
            UserContext user,
            int timezoneOffsetMinutes,
            string? objectName,
            string? objectAddress,
            CancellationToken cancellationToken = default)
            => _taskModification.UpdateTaskAsync(
                taskId, applyChanges, technicianId, user,
                timezoneOffsetMinutes, objectName, objectAddress, cancellationToken);

        public Task<TaskTimeUpdateResult> UpdateTaskTimeAsync(
            long taskId,
            DateTime targetDate,
            int targetSlotStartMinutes,
            int targetSlotEndMinutes,
            uint expectedVersion,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
            => _taskModification.UpdateTaskTimeAsync(
                taskId, targetDate, targetSlotStartMinutes, targetSlotEndMinutes,
                expectedVersion, user, timezoneOffsetMinutes, cancellationToken);

        public Task<TaskMoveResult> MoveTaskAsync(
            long taskId,
            long targetTechnicianId,
            DateTime targetDate,
            int targetSlotStartMinutes,
            uint expectedVersion,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
            => _taskModification.MoveTaskAsync(
                taskId, targetTechnicianId, targetDate, targetSlotStartMinutes,
                expectedVersion, user, timezoneOffsetMinutes, cancellationToken);

        public Task<TaskSwapResult> SwapTasksAsync(
            long sourceTaskId,
            long targetTaskId,
            uint sourceExpectedVersion,
            uint targetExpectedVersion,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default,
            bool recordHistory = true)
            => _taskModification.SwapTasksAsync(
                sourceTaskId, targetTaskId, sourceExpectedVersion, targetExpectedVersion,
                user, timezoneOffsetMinutes, cancellationToken, recordHistory);

        public Task<TaskSwapResult> SwapTasksSmartAsync(
            long sourceTaskId,
            long targetTaskId,
            uint sourceExpectedVersion,
            uint targetExpectedVersion,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
            => _taskModification.SwapTasksSmartAsync(
                sourceTaskId, targetTaskId, sourceExpectedVersion, targetExpectedVersion,
                user, timezoneOffsetMinutes, cancellationToken);

        public Task<TechnicianUpdateResult> TechnicianUpdateTaskAsync(
            long taskId,
            ScheduleTaskStatus status,
            string? comment,
            uint expectedVersion,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
            => _taskModification.TechnicianUpdateTaskAsync(
                taskId, status, comment, expectedVersion,
                user, timezoneOffsetMinutes, cancellationToken);

        // ── Task deletion ────────────────────────────────────────────────────

        public Task<TaskDeleteResult> DeleteTaskAsync(
            long taskId,
            uint expectedVersion,
            bool autoCompact,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
            => _taskDeletion.DeleteTaskAsync(
                taskId, expectedVersion, autoCompact,
                user, timezoneOffsetMinutes, cancellationToken);

        // ── Task previews ────────────────────────────────────────────────────

        public Task<DragPreviewResult> PreviewDragAsync(
            long taskId,
            long targetTechnicianId,
            DateTime targetDate,
            int targetSlotStartMinutes,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
            => _taskPreview.PreviewDragAsync(
                taskId, targetTechnicianId, targetDate, targetSlotStartMinutes,
                user, timezoneOffsetMinutes, cancellationToken);

        public Task<SwapSmartPreviewResult> PreviewSwapSmartAsync(
            long sourceTaskId,
            long targetTaskId,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
            => _taskPreview.PreviewSwapSmartAsync(
                sourceTaskId, targetTaskId, timezoneOffsetMinutes, cancellationToken);

        public Task ApplyAutomaticAssignmentsAsync(
            List<CreateTaskRequestDto> items,
            CancellationToken cancellationToken = default)
            => _assignmentMatching.ApplyAutomaticAssignmentsAsync(items, cancellationToken);

        public Task<AutoAssignmentSuggestionDto?> SuggestAutoAssignmentAsync(
            DateTime targetDate,
            int durationMinutes,
            string objectName,
            TaskPriority priority,
            int timezoneOffsetMinutes,
            List<SimulatedTaskAssignmentDto>? simulatedAssignments,
            Dictionary<long, int>? batchAssignmentCounts,
            CancellationToken cancellationToken = default)
            => _assignmentMatching.SuggestAutoAssignmentAsync(
                targetDate,
                durationMinutes,
                objectName,
                priority,
                timezoneOffsetMinutes,
                simulatedAssignments,
                batchAssignmentCounts,
                cancellationToken);
    }
}
