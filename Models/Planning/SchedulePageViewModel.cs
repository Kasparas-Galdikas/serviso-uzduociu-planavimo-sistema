using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Models.Planning
{
    public class SchedulePageViewModel
    {
        public DateTime WeekStart { get; set; }
        public DateTime SelectedDate { get; set; }
        public bool IsManager { get; set; }
        public List<Technician> Technicians { get; set; } = new();
        public List<TaskItem> Tasks { get; set; } = new();
        public List<DecisionOption> DecisionOptions { get; set; } = new();
    }
}
