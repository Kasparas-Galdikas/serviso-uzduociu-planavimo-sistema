using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public enum TaskSwapError
    {
        None,
        NotFound,
        TargetNotFound,
        NotEditable,
        Stale,
        InvalidSwap,
        Overlap,
        TravelInfeasible
    }

    public sealed record TaskSwapResult(
        TaskSwapError Error,
        string? Message = null,
        TaskEntity? SourceTask = null,
        TaskEntity? TargetTask = null,
        uint? CurrentSourceVersion = null,
        uint? CurrentTargetVersion = null)
    {
        public bool Ok => Error == TaskSwapError.None;
    }
}

