using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public enum TaskTimeUpdateError
    {
        None,
        NotFound,
        NotEditable,
        Stale,
        InvalidWindow,
        Overlap,
        TravelInfeasible
    }

    public sealed record TaskTimeUpdateResult(
        TaskTimeUpdateError Error,
        string? Message = null,
        TaskEntity? Task = null,
        uint? CurrentVersion = null)
    {
        public bool Ok => Error == TaskTimeUpdateError.None;
    }
}
