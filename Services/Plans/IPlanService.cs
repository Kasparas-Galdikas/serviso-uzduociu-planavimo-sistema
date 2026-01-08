using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Planning;

namespace ServiceTaskPlanningSystem.Services.Plans
{
    public interface IPlanService
    {
        Task<PlanEntity?> GetActivePlanAsync(CancellationToken cancellationToken = default);
        Task<PlanEntity?> GetDraftPlanAsync(CancellationToken cancellationToken = default);
        Task<PlanEntity> CreateDraftFromActiveAsync(DateTime? requestedStart = null, DateTime? requestedEnd = null, int? requestedWeeks = null, CancellationToken cancellationToken = default);
        Task<TaskCreateResult> CreateTaskAsync(int technicianId, DateTime targetDate, int startMinutes, int endMinutes, string title, string location, TaskPriority priority, ScheduleTaskStatus status, string role, CancellationToken cancellationToken = default);
        Task<TaskEntity> UpdateTaskAsync(long taskId, Action<TaskEntity> applyChanges, string role, CancellationToken cancellationToken = default);
        Task<TaskMoveResult> MoveTaskAsync(long taskId, int targetTechnicianId, DateTime targetDate, int targetSlotStartMinutes, uint expectedVersion, string role, CancellationToken cancellationToken = default);
        Task PublishDraftAsync(CancellationToken cancellationToken = default);
        Task DiscardDraftAsync(CancellationToken cancellationToken = default);
    }
}
