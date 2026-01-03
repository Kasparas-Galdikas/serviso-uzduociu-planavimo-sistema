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

        public TaskEntity Task { get; set; } = default!;
    }
}
