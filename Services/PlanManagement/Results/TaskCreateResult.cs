using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public enum TaskCreateError
    {
        None,
        NotEditable,
        InvalidWindow,
        DraftWindowMismatch,
        Overlap,
        TechnicianNotFound,
        TravelInfeasible
    }

    public sealed record TaskCreateResult(
        TaskCreateError Error,
        string? Message = null,
        TaskEntity? Task = null,
        uint? CurrentVersion = null)
    {
        public bool Ok => Error == TaskCreateError.None;
    }
}

