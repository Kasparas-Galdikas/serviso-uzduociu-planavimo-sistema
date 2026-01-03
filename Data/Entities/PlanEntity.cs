using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Data
{
    public class PlanEntity
    {
        public long Id { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int Weeks { get; set; }
        public long? CreatedBy { get; set; }
        public PlanStatus Status { get; set; }
        public long? SupersededByPlanId { get; set; }
        public DateTime CreatedAt { get; set; }

        public UserEntity? CreatedByUser { get; set; }
        public PlanEntity? SupersededByPlan { get; set; }
        public ICollection<PlanEntity> SupersededPlans { get; set; } = new List<PlanEntity>();
        public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
    }
}
