using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Data
{
    public class AddressEntity
    {
        public long Id { get; set; }
        public long RegionId { get; set; }
        public string Street { get; set; } = string.Empty;
        public string FullAddress { get; set; } = string.Empty;
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public DateTime CreatedAt { get; set; }

        public RegionEntity Region { get; set; } = default!;
        public ICollection<TechnicianEntity> Technicians { get; set; } = new List<TechnicianEntity>();
        public ICollection<ServiceObjectEntity> ServiceObjects { get; set; } = new List<ServiceObjectEntity>();
    }
}
