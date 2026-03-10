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

    /// <summary>
    /// Narrows the reason for an <see cref="TaskSwapError.InvalidSwap"/> result so callers
    /// can decide whether a smart-planner fallback makes sense.
    /// </summary>
    public enum InvalidSwapReason
    {
        None,
        /// <summary>Task duration would overflow the work window at the target slot.
        /// The smart planner may be able to find an earlier feasible slot.</summary>
        WorkWindow,
        SameTask,
        DifferentTechnician,
        DifferentDate,
        DifferentPlan,
        CanceledTask,
        InvalidDuration,
        SpansMidnight,
        /// <summary>One or both tasks (or the travel leg leading into them) would land in the past.
        /// The smart planner cannot recover this — it is a hard constraint.</summary>
        PastTime
    }

    public sealed record TaskSwapResult(
        TaskSwapError Error,
        string? Message = null,
        TaskEntity? SourceTask = null,
        TaskEntity? TargetTask = null,
        uint? CurrentSourceVersion = null,
        uint? CurrentTargetVersion = null,
        InvalidSwapReason InvalidSwapReason = InvalidSwapReason.None)
    {
        public bool Ok => Error == TaskSwapError.None;
    }
}

