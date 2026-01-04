using System;

namespace ServiceTaskPlanningSystem.Models.Planning
{
    public class TaskItem
    {
        public int Id { get; set; }
        public long PlanId { get; set; }
        public int TechnicianId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public string Priority { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsConflict { get; set; }
        public bool IsLoad { get; set; }
        public bool IsTravelSwitch { get; set; }
        public bool IsNewPlan { get; set; }
        public TaskPlanSource Source { get; set; } = TaskPlanSource.Active;
        public bool IsEditable { get; set; }
        public string? Risk { get; set; }
    }

    public enum TaskPlanSource
    {
        Active,
        Draft
    }
}
