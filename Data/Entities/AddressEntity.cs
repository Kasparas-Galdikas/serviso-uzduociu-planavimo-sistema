using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Data
{
    public class AddressEntity
    {
        public long Id { get; set; }
        public string FullAddress { get; set; } = string.Empty;
        public string? Street { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public DateTime CreatedAt { get; set; }

        public ICollection<TechnicianEntity> Technicians { get; set; } = new List<TechnicianEntity>();
        public ICollection<ObjectEntity> Objects { get; set; } = new List<ObjectEntity>();
    }
}
