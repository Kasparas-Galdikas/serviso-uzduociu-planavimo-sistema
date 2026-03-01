using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Models.Planning
{
    /// <summary>
    /// Data for rendering a technician row header in the schedule table.
    /// </summary>
    public class TechnicianHeaderInfo
    {
        public int TechnicianId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
    }

    /// <summary>
    /// Data for rendering an empty time slot in the daily schedule.
    /// </summary>
    public class EmptySlotInfo
    {
        public string Date { get; set; } = string.Empty;
        public int TechnicianId { get; set; }
        public int SlotStartMinutes { get; set; }
        public string TimeLabel { get; set; } = string.Empty;
    }

    /// <summary>
    /// Data for rendering a daily schedule cell (contains tasks or empty slot).
    /// </summary>
    public class DailyCellData
    {
        public List<TaskItem> Tasks { get; set; } = new List<TaskItem>();
        public EmptySlotInfo? EmptySlot { get; set; }
        public bool IsEmpty => Tasks.Count == 0 && EmptySlot != null;
    }

    /// <summary>
    /// Data for rendering a weekly summary cell.
    /// </summary>
    public class WeeklyCellData
    {
        public string DailyUrl { get; set; } = string.Empty;
        public double TotalHours { get; set; }
        public int TaskCount { get; set; }
        public double DailyLimit { get; set; }
        public double FreeHours { get; set; }
        public bool HasConflicts { get; set; }
        public bool HasLoadFlags { get; set; }
        public string StateClass { get; set; } = "task-normal";
    }
}
