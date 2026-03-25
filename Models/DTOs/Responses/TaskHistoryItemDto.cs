using System;

namespace ServiceTaskPlanningSystem.Models.DTOs.Responses
{
    public class TaskHistoryItemDto
    {
        public long Id { get; set; }
        public long? TaskId { get; set; }
        public string ActionType { get; set; } = string.Empty;
        public string Outcome { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public DateTime OccurredAt { get; set; }
        public string ActorDisplayName { get; set; } = string.Empty;
        public string ActorRole { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string? DetailsJson { get; set; }
    }
}
