using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Data
{
    public class ObjectEntity
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public long? AddressId { get; set; }
        public long? RegionId { get; set; }
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; }

        public RegionEntity? Region { get; set; }
        public AddressEntity? AddressEntity { get; set; }
        public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
    }
}
