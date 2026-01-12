using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Data
{
    public class TechnicianEntity
    {
        public long Id { get; set; }
        public long? UserId { get; set; }
        public long? RegionId { get; set; }
        public decimal DailyLimit { get; set; }
        public decimal WeeklyLimit { get; set; }
        public int TravelSwitchLimit { get; set; }
        public string? RulesSummary { get; set; }
        public DateTime CreatedAt { get; set; }

        public UserEntity? User { get; set; }
        public RegionEntity? Region { get; set; }
        public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
    }
}
