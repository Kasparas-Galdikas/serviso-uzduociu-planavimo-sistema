using System;

namespace ServiceTaskPlanningSystem.Data
{
    public class TechnicianUnavailabilityEntity
    {
        public long Id { get; set; }
        public long TechnicianId { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public DateTime CreatedAt { get; set; }

        public TechnicianEntity Technician { get; set; } = default!;
    }
}
