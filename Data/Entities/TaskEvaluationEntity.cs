using System;

namespace ServiceTaskPlanningSystem.Data
{
    public class TaskEvaluationEntity
    {
        public long TaskId { get; set; }
        public RiskLevel? RiskLevel { get; set; }
        public bool IsConflict { get; set; }
        public bool IsOverloaded { get; set; }
        public bool TravelRequired { get; set; }
        public decimal? LoadHours { get; set; }
        public DateTime EvaluatedAt { get; set; }
        public bool? HasConflict { get; set; }
        public string? ConflictReason { get; set; }
        public decimal? DailyHours { get; set; }
        public decimal? WeeklyHours { get; set; }
        public decimal? OverloadHours { get; set; }
        public int? SwitchCount { get; set; }
        public LoadState? Status { get; set; }
        public bool IsTravelInfeasible { get; set; }
        public int? RequiredTravelMinutes { get; set; }
        public int? AvailableGapMinutes { get; set; }

        public TaskEntity Task { get; set; } = default!;
    }
}
