namespace ServiceTaskPlanningSystem.Data
{
    /// <summary>
    /// Task execution status lifecycle:
    /// - InProgress: Default for new tasks (assigned and awaiting execution)
    /// - Completed: Technician marked as successfully finished
    /// - Conflict: Technician reported an issue/conflict (requires comment)
    /// - Canceled: Auto-expired after end time without technician action, or manually canceled
    /// </summary>
    public enum ScheduleTaskStatus
    {
        /// <summary>
        /// Task is assigned and in progress (default for new tasks)
        /// </summary>
        InProgress = 0,

        /// <summary>
        /// Task completed successfully by technician
        /// </summary>
        Completed = 1,

        /// <summary>
        /// Technician reported a conflict/issue with the task
        /// </summary>
        Conflict = 2,

        /// <summary>
        /// Task was canceled or auto-expired
        /// </summary>
        Canceled = 3
    }
}
