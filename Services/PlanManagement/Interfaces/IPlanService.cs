using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public interface IPlanService
    {
        Task<PlanEntity?> GetActivePlanAsync(CancellationToken cancellationToken = default);
        Task<PlanEntity?> GetDraftPlanAsync(CancellationToken cancellationToken = default);
        Task<PlanEntity> CreateDraftFromActiveAsync(DateTime? requestedStart = null, DateTime? requestedEnd = null, int? requestedWeeks = null, CancellationToken cancellationToken = default);
        Task<TaskCreateResult> CreateTaskAsync(int technicianId, DateTime targetDate, int startMinutes, int endMinutes, string title, string location, TaskPriority priority, ScheduleTaskStatus status, string skillLevel, string objectName, string objectAddress, string instructions, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task<TaskEntity> UpdateTaskAsync(long taskId, Action<TaskEntity> applyChanges, long technicianId, UserContext user, int timezoneOffsetMinutes, string? objectName, string? objectAddress, CancellationToken cancellationToken = default);
        Task<TaskMoveResult> MoveTaskAsync(long taskId, int targetTechnicianId, DateTime targetDate, int targetSlotStartMinutes, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task<TaskTimeUpdateResult> UpdateTaskTimeAsync(long taskId, DateTime targetDate, int targetSlotStartMinutes, int targetSlotEndMinutes, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task<TaskDeleteResult> DeleteTaskAsync(long taskId, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default);
        Task PublishDraftAsync(CancellationToken cancellationToken = default);
        Task DiscardDraftAsync(CancellationToken cancellationToken = default);
    }
}
