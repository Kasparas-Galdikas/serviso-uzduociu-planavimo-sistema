using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.Plans
{
    public enum TaskCreateError
    {
        None,
        NotEditable,
        InvalidWindow,
        Overlap,
        TechnicianNotFound,
        DraftNotFound
    }

    public sealed record TaskCreateResult(
        TaskCreateError Error,
        string? Message = null,
        TaskEntity? Task = null)
    {
        public bool Ok => Error == TaskCreateError.None;
    }
}

