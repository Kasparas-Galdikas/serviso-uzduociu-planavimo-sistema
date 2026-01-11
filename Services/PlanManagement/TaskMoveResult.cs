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
        TechnicianNotFound
    }

    public sealed record TaskMoveResult(
        TaskMoveError Error,
        string? Message = null,
        TaskEntity? Task = null,
        uint? CurrentVersion = null)
    {
        public bool Ok => Error == TaskMoveError.None;
    }
}

