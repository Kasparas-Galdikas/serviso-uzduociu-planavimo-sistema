using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;

namespace ServiceTaskPlanningSystem.Services.Travel
{
    public interface ITaskTravelFeasibilityService
    {
        Task<TravelFeasibilityResult> EvaluatePlacementAsync(
            long technicianId,
            DateTime proposedStartAt,
            DateTime proposedEndAt,
            long serviceAddressId,
            int proposedStartMinutesLocal,
            long? excludeTaskId,
            bool checkOverlap,
            CancellationToken cancellationToken = default,
            IReadOnlyList<ProposedRouteStop>? simulatedStops = null);
    }

    public sealed class TravelFeasibilityResult
    {
        public bool IsFeasible { get; init; }
        public bool HasOverlap { get; init; }
        public int? InboundTravelMinutes { get; init; }
        public int? OutboundTravelMinutes { get; init; }
        public string? Message { get; init; }
    }
}
