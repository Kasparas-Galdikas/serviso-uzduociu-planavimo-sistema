using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public interface IPlanService
    {
        /// <summary>
        /// Gets workload snapshot for a technician on a specific day.
        /// Calculates daily and weekly hours, utilization, and conflict status.
        /// </summary>
        Task<LoadSnapshotDto> GetWorkloadSnapshotAsync(long technicianId, DateTime dayStartUtc, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets task details with plan and object information, including version.
        /// </summary>
        Task<TaskDetailResult?> GetTaskDetailAsync(long taskId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the current version (xmin) of a task.
        /// </summary>
        Task<uint> GetTaskVersionAsync(long taskId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if a technician exists.
        /// </summary>
        Task<bool> TechnicianExistsAsync(long technicianId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if a task would overlap with existing tasks for a technician.
        /// </summary>
        Task<bool> HasTaskOverlapAsync(long excludeTaskId, long technicianId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates a task's status and comment (for technician use).
        /// </summary>
        Task<TechnicianUpdateResult> TechnicianUpdateTaskAsync(long taskId, ScheduleTaskStatus status, string? comment, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);

        Task<PlanEntity?> GetActivePlanAsync(CancellationToken cancellationToken = default);
        Task<PlanEntity?> GetDraftPlanAsync(CancellationToken cancellationToken = default);
        Task<PlanEntity> EnsureBacklogDraftAsync(DateTime? requestedStart = null, DateTime? requestedEnd = null, int? requestedWeeks = null, CancellationToken cancellationToken = default);
        Task ApplyDraftToActiveAsync(CancellationToken cancellationToken = default);
        Task<TaskCreateResult> CreateTaskAsync(long technicianId, DateTime targetDate, int startMinutes, int endMinutes, string title, TaskPriority priority, ScheduleTaskStatus status, string skillLevel, string objectName, string objectAddress, string instructions, UserContext user, int timezoneOffsetMinutes, long? sourceBacklogItemId = null, CancellationToken cancellationToken = default);
        Task<TaskCreateResult> CreateDraftTaskAsync(long technicianId, DateTime targetDate, int startMinutes, int endMinutes, string title, TaskPriority priority, ScheduleTaskStatus status, string skillLevel, string objectName, string objectAddress, string instructions, UserContext user, int timezoneOffsetMinutes, long? sourceBacklogItemId = null, CancellationToken cancellationToken = default);
        Task<BulkCreateTaskResponseDto> BulkCreateTasksAsync(IReadOnlyList<CreateTaskRequestDto> items, string? planMode, UserContext user, CancellationToken cancellationToken = default);
        Task<BulkCreateTaskResponseDto> BulkValidateTasksAsync(IReadOnlyList<CreateTaskRequestDto> items, string? planMode, UserContext user, string? planScope = null, CancellationToken cancellationToken = default);
        Task<BulkSchedulePreviewResponseDto> PreviewBulkScheduleAsync(IReadOnlyList<CreateTaskRequestDto> items, string? planMode, UserContext user, CancellationToken cancellationToken = default);
        Task<TaskUpdateResult> UpdateTaskAsync(long taskId, Action<TaskEntity> applyChanges, long technicianId, UserContext user, int timezoneOffsetMinutes, string? objectName, string? objectAddress, CancellationToken cancellationToken = default);
        Task<TaskMoveResult> MoveTaskAsync(long taskId, long targetTechnicianId, DateTime targetDate, int targetSlotStartMinutes, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task<TaskSwapResult> SwapTasksAsync(long sourceTaskId, long targetTaskId, uint sourceExpectedVersion, uint targetExpectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default, bool recordHistory = true);
        Task<TaskSwapResult> SwapTasksSmartAsync(long sourceTaskId, long targetTaskId, uint sourceExpectedVersion, uint targetExpectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task<TaskTimeUpdateResult> UpdateTaskTimeAsync(long taskId, DateTime targetDate, int targetSlotStartMinutes, int targetSlotEndMinutes, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task<TaskDeleteResult> DeleteTaskAsync(long taskId, uint expectedVersion, bool autoCompact, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task<(int Deleted, int Skipped)> BulkDeleteTasksAsync(long[] ids, UserContext user, CancellationToken cancellationToken = default);
        Task DiscardDraftAsync(CancellationToken cancellationToken = default);
        Task<(long? ActivePlanId, long? DraftPlanId)> RecalculateCurrentPlansAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Previews a drag operation for a task to calculate workload impact.
        /// </summary>
        Task<DragPreviewResult> PreviewDragAsync(
            long taskId,
            long targetTechnicianId,
            DateTime targetDate,
            int targetSlotStartMinutes,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Dry-run feasibility check for a smart swap without persisting any changes.
        /// Returns whether the swap is feasible and, if the smart planner would be needed,
        /// what adjustments it would produce.
        /// </summary>
        Task<SwapSmartPreviewResult> PreviewSwapSmartAsync(
            long sourceTaskId,
            long targetTaskId,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Finds the best-fit technician for a new task based on workload scoring.
        /// Zero writes. Returns null if no suitable technician can be found.
        /// </summary>
        Task<AutoAssignmentSuggestionDto?> SuggestAutoAssignmentAsync(
            DateTime targetDate,
            int durationMinutes,
            long? objectId,
            string objectName,
            TaskPriority priority,
            int timezoneOffsetMinutes,
            List<SimulatedTaskAssignmentDto>? simulatedAssignments,
            Dictionary<long, int>? batchAssignmentCounts,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Mutates items in-place with auto-assigned TechnicianId, StartMinutes, EndMinutes.
        /// Items processed in descending priority order. Items already having a TechnicianId are skipped.
        /// Zero writes.
        /// </summary>
        Task ApplyAutomaticAssignmentsAsync(
            List<CreateTaskRequestDto> items,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns one suggestion per input item (null where no assignment could be found).
        /// Processes items in descending priority order with chained simulated assignments so
        /// earlier high-priority items influence later ones — same logic as ApplyAutomaticAssignmentsAsync
        /// but returns suggestions instead of mutating the list. Zero writes.
        /// Result array is index-aligned with the input list.
        /// </summary>
        Task<IReadOnlyList<AutoAssignmentSuggestionDto?>> SuggestBulkAutoAssignmentsAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the next free start minute for a technician on a date, chained after
        /// afterEndMinutes (or earliest creatable if null), skipping over existing scheduled tasks.
        /// Used to prefill bulk scheduling forms with chain-aware, conflict-free suggested times.
        /// </summary>
        Task<(DateTime TargetDate, int StartMinutes, int EndMinutes)> GetSuggestedNextSlotAsync(
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
            CancellationToken cancellationToken = default);

        // ── Technician unavailability / conflict resolution ──────────────────

        /// <summary>
        /// Returns a preview of InProgress tasks that would be displaced for the given
        /// technician and period. Zero writes — safe to call speculatively.
        /// </summary>
        Task<UnavailabilityPreviewResult> PreviewDisplacementAsync(
            long technicianId,
            DateTime periodStart,
            DateTime periodEnd,
            UserContext actorUser,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically displaces all InProgress tasks for a technician in the given period
        /// to high-priority backlog items, deletes the original tasks, and records the
        /// unavailability period — all in one transaction.
        /// </summary>
        Task<DisplaceTasksResult> DisplaceTasksToBacklogAsync(
            long technicianId,
            DateTime periodStart,
            DateTime periodEnd,
            UserContext actorUser,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default);

        Task<DisplaceTasksResult> UpdateUnavailabilityAsync(
            long id,
            DateTime newPeriodStart,
            DateTime newPeriodEnd,
            UserContext actorUser,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default);

        Task<DisplaceTasksResult> DeleteUnavailabilityAsync(
            long id,
            UserContext actorUser,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default);
    }
}
