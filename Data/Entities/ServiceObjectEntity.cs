using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Data
{
    public class ServiceObjectEntity
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public long AddressId { get; set; }
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; }

        public AddressEntity Address { get; set; } = default!;
        public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
    }
}
