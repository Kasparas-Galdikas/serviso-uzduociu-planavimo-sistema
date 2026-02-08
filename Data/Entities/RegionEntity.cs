using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Data
{
    public class RegionEntity
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public int PriorityWeight { get; set; }
        public DateTime CreatedAt { get; set; }

        public ICollection<TechnicianEntity> Technicians { get; set; } = new List<TechnicianEntity>();
        public ICollection<AddressEntity> Addresses { get; set; } = new List<AddressEntity>();
    }
}
