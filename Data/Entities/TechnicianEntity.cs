using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Data
{
    public class TechnicianEntity
    {
        public long Id { get; set; }
        public long? UserId { get; set; }
        public long WorkplaceAddressId { get; set; }
        public AddressEntity WorkplaceAddress { get; set; } = default!;
        public decimal DailyLimit { get; set; }
        public decimal WeeklyLimit { get; set; }
        public string? RulesSummary { get; set; }
        public DateTime CreatedAt { get; set; }

        public UserEntity? User { get; set; }
        public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
    }
}
