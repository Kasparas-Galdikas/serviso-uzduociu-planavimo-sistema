using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Data
{
    public class AddressEntity
    {
        public long Id { get; set; }
        public string City { get; set; } = string.Empty;
        public string Street { get; set; } = string.Empty;
        public string FullAddress { get; set; } = string.Empty;
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public decimal? SnappedLatitude  { get; set; }
        public decimal? SnappedLongitude { get; set; }
        public DateTime CreatedAt { get; set; }

        public ICollection<TechnicianEntity> Technicians { get; set; } = new List<TechnicianEntity>();
        public ICollection<ServiceObjectEntity> ServiceObjects { get; set; } = new List<ServiceObjectEntity>();
    }
}
