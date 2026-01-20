using System;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
  
    public abstract class PlanChange
    {
     
        public abstract ChangeType Type { get; }

        public long TargetTaskId { get; init; }

        
        public abstract string Description { get; }

        /// <summary>
        /// Why this change is suggested.
        /// </summary>
        public virtual string Rationale => string.Empty;

       
        public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    }

    
    public enum ChangeType
    {
     
        TimeShift = 1,

     
        TechnicianSwap = 2,

        DayShift = 3,

      
        SplitTask = 4,

     
        MergeTasks = 5,

     
        ReorderTasks = 6
    }
}
