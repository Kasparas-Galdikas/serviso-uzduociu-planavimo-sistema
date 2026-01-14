namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Defines the context for task generation.
    /// Describes WHAT needs improvement and in WHAT scope.
    /// </summary>
    public sealed class GeneratorContext
    {
        /// <summary>
        /// Task that triggered generation (problematic or selected task).
        /// </summary>
        public long TargetTaskId { get; init; }

        /// <summary>
        /// Scope in which generation should occur.
        /// </summary>
        public GeneratorScope Scope { get; init; }

        /// <summary>
        /// Maximum number of candidate plans to return.
        /// </summary>
        public int MaxCandidates { get; init; } = 5;
    }
}
