using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public enum TaskMoveError
    {
        None,
        NotFound,
        NotEditable,
        Stale,
        InvalidWindow,
        Overlap,
        TechnicianNotFound,
        TravelInfeasible
    }

    public sealed record TaskMoveResult(
        TaskMoveError Error,
        string? Message = null,
        TaskEntity? Task = null,
        uint? CurrentVersion = null,
        DateTime? OldStartAt = null,
        long? OldTechnicianId = null,
        string? NewTechnicianName = null)
    {
        public bool Ok => Error == TaskMoveError.None;
    }
}
