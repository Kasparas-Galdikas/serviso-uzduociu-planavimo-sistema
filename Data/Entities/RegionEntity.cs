using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Data
{
    public class RegionEntity
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int PriorityWeight { get; set; }
        public DateTime CreatedAt { get; set; }

        public ICollection<TechnicianEntity> Technicians { get; set; } = new List<TechnicianEntity>();
    }
}
