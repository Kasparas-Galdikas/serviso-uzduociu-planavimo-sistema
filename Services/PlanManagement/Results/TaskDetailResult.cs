using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Result containing task details with version information.
    /// </summary>
    public sealed record TaskDetailResult(
        TaskEntity Task,
        uint Version);
}
