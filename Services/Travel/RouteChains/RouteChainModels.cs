using System;
using System.Collections.Generic;
using System.Linq;

namespace ServiceTaskPlanningSystem.Services.Travel.RouteChains
{
    public sealed class RouteChainRequest
    {
        public long TechnicianId { get; init; }
        public DateTime DayUtc { get; init; }
        public bool IncludeCanceled { get; init; }
        public bool FilterByPlanId { get; init; }
        public long? PlanId { get; init; }
        public long? ExcludeTaskId { get; init; }
        public ProposedRouteStop? ProposedStop { get; init; }
    }

    public sealed class ProposedRouteStop
    {
        public DateTime StartAt { get; init; }
        public DateTime EndAt { get; init; }
        public long AddressId { get; init; }
    }

    public sealed class RouteStop
    {
        public long? TaskId { get; init; }
        public DateTime StartAt { get; init; }
        public DateTime EndAt { get; init; }
        public long AddressId { get; init; }
        public bool IsProposed { get; init; }
    }

    public sealed class RouteChain
    {
        public long TechnicianId { get; init; }
        public long WorkplaceAddressId { get; init; }
        public List<RouteStop> Stops { get; init; } = new();

        public List<long> BuildAddressPathIds()
        {
            var path = new List<long> { WorkplaceAddressId };
            path.AddRange(Stops.Select(s => s.AddressId));
            return path;
        }
    }
}
