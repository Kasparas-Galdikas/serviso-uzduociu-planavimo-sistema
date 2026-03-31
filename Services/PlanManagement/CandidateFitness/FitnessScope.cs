namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Defines how the assignment fitness engine is being used.
    /// </summary>
    public enum FitnessScope
    {

        /// User manually edits or creates tasks.
        /// Fitness engine only validates and suggests.
        Manual,

        /// Automatic task generation (initial or batch planning).
        Automatic,

        /// Optimization / rebalancing of existing plans.
        Optimization
    }
}
