using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Represents a hypothetical plan modification suggestion.
    /// This is NOT a persisted entity.
    /// </summary>
    public sealed class CandidatePlan
    {
        // Identity
        public long TargetTaskId { get; init; }

        // Description for UI / planner
        public string Description { get; init; } = string.Empty;

        // Evaluation results
        public LoadState LoadState { get; init; }
        public double LoadScore { get; init; }

        // Optional metadata (future use)
        public int AffectedTasks { get; init; }
    }
}
