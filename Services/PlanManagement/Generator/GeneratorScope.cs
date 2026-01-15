namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Defines how the task generator is being used.
    /// </summary>
    public enum GeneratorScope
    {

        /// User manually edits or creates tasks.
        /// Generator only validates and suggests.
        Manual,

        /// Automatic task generation (initial or batch planning).
        Automatic,

        /// Optimization / rebalancing of existing plans.
        Optimization
    }
}
