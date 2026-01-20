using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public enum TaskDeleteError
    {
        None,
        NotFound,
        NotEditable,
        Stale
    }

    public sealed record TaskDeleteResult(
        TaskDeleteError Error,
        string? Message = null,
        TaskEntity? Task = null,
        uint? CurrentVersion = null)
    {
        public bool Ok => Error == TaskDeleteError.None;
    }
}
