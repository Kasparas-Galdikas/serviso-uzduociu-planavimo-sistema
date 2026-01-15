using System;

namespace ServiceTaskPlanningSystem.Data
{
    public class TravelRouteEntity
    {
        public long Id { get; set; }
        public long FromAddressId { get; set; }
        public long ToAddressId { get; set; }
        public decimal DistanceKm { get; set; }
        public int DurationMinutes { get; set; }
        public DateTime CalculatedAt { get; set; }

        public AddressEntity FromAddress { get; set; } = default!;
        public AddressEntity ToAddress { get; set; } = default!;
    }
}
