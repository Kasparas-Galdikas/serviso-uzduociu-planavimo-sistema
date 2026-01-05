namespace ServiceTaskPlanningSystem.Infrastructure.Logging
{
    public class TraceFilterOptions
    {
        /// <summary>
        /// If true, EF Core command logs will be skipped from the in-memory trace store.
        /// </summary>
        public bool SuppressEfCommands { get; set; } = true;

        /// <summary>
        /// If true, duplicate messages within the same span will be skipped.
        /// </summary>
        public bool DeduplicatePerSpan { get; set; } = true;

        /// <summary>
        /// Maximum entries per trace to keep in the store.
        /// </summary>
        public int MaxEntriesPerTrace { get; set; } = 200;
    }
}
