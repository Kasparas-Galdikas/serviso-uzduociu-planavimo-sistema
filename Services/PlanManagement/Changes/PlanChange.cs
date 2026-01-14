using System;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Base class for plan modification suggestions.
    /// Allows different types of changes (move, swap, split, merge, etc.)
    /// 
    /// Future extensions:
    /// - SplitTaskChange (divide long task)
    /// - MergeTasksChange (combine related tasks)
    /// - ReorderChainChange (optimize task sequence)
    /// - BatchChange (multiple changes together)
    /// </summary>
    public abstract class PlanChange
    {
        /// <summary>
        /// Type of change being suggested.
        /// </summary>
        public abstract ChangeType Type { get; }

        /// <summary>
        /// Task being modified.
        /// </summary>
        public long TargetTaskId { get; init; }

        /// <summary>
        /// Human-readable description of what changes.
        /// </summary>
        public abstract string Description { get; }

        /// <summary>
        /// Why this change is suggested.
        /// </summary>
        public virtual string Rationale => string.Empty;

        /// <summary>
        /// When this change was generated.
        /// </summary>
        public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Types of plan modifications the generator can suggest.
    /// </summary>
    public enum ChangeType
    {
        /// <summary>
        /// Move task to different time on same day/technician
        /// </summary>
        TimeShift = 1,

        /// <summary>
        /// Assign task to different technician (same time)
        /// </summary>
        TechnicianSwap = 2,

        /// <summary>
        /// Move task to different day (same technician)
        /// </summary>
        DayShift = 3,

        /// <summary>
        /// Split long task into multiple shorter tasks
        /// </summary>
        SplitTask = 4,

        /// <summary>
        /// Merge related tasks together
        /// </summary>
        MergeTasks = 5,

        /// <summary>
        /// Reorder tasks within day for optimal flow
        /// </summary>
        ReorderTasks = 6
    }
}
