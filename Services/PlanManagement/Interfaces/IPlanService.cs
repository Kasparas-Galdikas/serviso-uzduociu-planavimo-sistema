using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Data;
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
        Task<PlanEntity> CreateDraftFromActiveAsync(DateTime? requestedStart = null, DateTime? requestedEnd = null, int? requestedWeeks = null, CancellationToken cancellationToken = default);
        Task<TaskCreateResult> CreateTaskAsync(long technicianId, DateTime targetDate, int startMinutes, int endMinutes, string title, TaskPriority priority, ScheduleTaskStatus status, string skillLevel, string objectName, string objectAddress, string instructions, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task<TaskUpdateResult> UpdateTaskAsync(long taskId, Action<TaskEntity> applyChanges, long technicianId, UserContext user, int timezoneOffsetMinutes, string? objectName, string? objectAddress, CancellationToken cancellationToken = default);
        Task<TaskMoveResult> MoveTaskAsync(long taskId, long targetTechnicianId, DateTime targetDate, int targetSlotStartMinutes, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task<TaskSwapResult> SwapTasksAsync(long sourceTaskId, long targetTaskId, uint sourceExpectedVersion, uint targetExpectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task<TaskSwapResult> SwapTasksSmartAsync(long sourceTaskId, long targetTaskId, uint sourceExpectedVersion, uint targetExpectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task<TaskTimeUpdateResult> UpdateTaskTimeAsync(long taskId, DateTime targetDate, int targetSlotStartMinutes, int targetSlotEndMinutes, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task<TaskDeleteResult> DeleteTaskAsync(long taskId, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task PublishDraftAsync(CancellationToken cancellationToken = default);
        Task DiscardDraftAsync(CancellationToken cancellationToken = default);

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
    }
}


