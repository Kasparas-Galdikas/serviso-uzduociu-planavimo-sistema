using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Models.Planning
{
    public class SchedulePageViewModel
    {
        public DateTime WeekStart { get; set; }
        public DateTime SelectedDate { get; set; }
        public bool IsManager { get; set; }
        public string ViewMode { get; set; } = "day"; // "day" or "week"
        public List<Technician> Technicians { get; set; } = new();
        public List<TaskItem> Tasks { get; set; } = new();
        public List<DecisionOption> DecisionOptions { get; set; } = new();
        public List<string> AvailableLocations { get; set; } = new();
        public List<string> AvailableStatuses { get; set; } = new();
    }
}
