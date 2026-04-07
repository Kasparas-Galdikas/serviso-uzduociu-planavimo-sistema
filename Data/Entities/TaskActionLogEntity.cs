using System;

namespace ServiceTaskPlanningSystem.Data
{
    public class TaskActionLogEntity
    {
        public long Id { get; set; }
        public long? TaskId { get; set; }
        public long? PlanId { get; set; }
        public string ActionType { get; set; } = string.Empty;
        public string Outcome { get; set; } = "success";
        public long? ActorUserId { get; set; }
        public string ActorName { get; set; } = string.Empty;
        public string ActorRole { get; set; } = string.Empty;
        public DateTime OccurredAtUtc { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string? DetailsJson { get; set; }
        public string Source { get; set; } = "user";
    }
}
