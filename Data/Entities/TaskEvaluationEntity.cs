using System;

namespace ServiceTaskPlanningSystem.Data
{
    public class TaskEvaluationEntity
    {
        public long TaskId { get; set; }
        public decimal? OverloadHours { get; set; }
        public LoadState? Status { get; set; }

        public TaskEntity Task { get; set; } = default!;
    }
}
