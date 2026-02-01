using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public enum TechnicianUpdateError
    {
        None,
        TaskNotFound,
        NotOwner,
        NotEditable,
        InvalidStatus,
        Stale
    }

    public sealed record TechnicianUpdateResult(
        TechnicianUpdateError Error,
        string? Message = null,
        TaskEntity? Task = null,
        uint? CurrentVersion = null)
    {
        public bool Ok => Error == TechnicianUpdateError.None;
    }
}
