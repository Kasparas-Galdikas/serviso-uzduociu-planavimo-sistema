using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public enum TaskUpdateError
    {
        None,
        NotFound,
        NotEditable,
        TechnicianNotFound,
        Overlap,
        InvalidWindow,
        TravelInfeasible
    }

    public sealed record TaskUpdateResult(
        TaskUpdateError Error,
        string? Message = null,
        TaskEntity? Task = null)
    {
        public bool Ok => Error == TaskUpdateError.None;
    }
}

