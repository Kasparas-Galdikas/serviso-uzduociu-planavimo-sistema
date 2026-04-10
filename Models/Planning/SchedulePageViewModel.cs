using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Models.Planning
{
    public class SchedulePageViewModel
    {
        public DateTime WeekStart { get; set; }
        public DateTime SelectedDate { get; set; }
        public bool IsManager { get; set; }
        public long? CurrentUserId { get; set; }
        public bool HasDraft { get; set; }
        public bool IsPlanningMode { get; set; }
        public long? CurrentPlanId { get; set; }
        public string? CurrentPlanStatus { get; set; }
        public string ViewMode { get; set; } = "day"; // "day" or "week"
        public string InformationType { get; set; } = "apkrova"; // "apkrova" or "darbo-statusas"
        public long? SelectedTechnicianId { get; set; }
        public List<Technician> Technicians { get; set; } = new();
        public List<TaskItem> Tasks { get; set; } = new();
        public List<DecisionOption> DecisionOptions { get; set; } = new();
        public List<IssueItem> Issues { get; set; } = new();
        public List<string> AvailableLocations { get; set; } = new();
        public List<string> AvailableStatuses { get; set; } = new();
        public ScheduleSummary? Summary { get; set; }
        public DailyTableLayout? DailyLayout { get; set; }
        public WeeklyTableLayout? WeeklyLayout { get; set; }
        public bool IsPreviewMode { get; set; }
        public DateTime PlannedPeriodStart { get; set; }
        public DateTime PlannedPeriodEnd { get; set; }
        public int PlannedWeeks { get; set; } = 1;
        public DateTime FirstFreeDate { get; set; }
        public int EarliestStartMinutes { get; set; }
        public DateTime EarliestCreatableTargetDate { get; set; }
        public int EarliestCreatableStartMinutes { get; set; }
        public int EarliestCreatableEndMinutes { get; set; }
    }
}
