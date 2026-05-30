using System;
using System.Collections.Generic;
using ServiceTaskPlanningSystem.Data.Enums;

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
        public ExperienceLevel ExperienceLevel { get; set; } = ExperienceLevel.Mid;
        public DateTime CreatedAt { get; set; }

        public UserEntity? User { get; set; }
        public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
    }
}
