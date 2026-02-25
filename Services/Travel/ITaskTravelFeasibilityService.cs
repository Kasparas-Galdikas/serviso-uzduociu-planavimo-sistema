using System;
using System.Threading;
using System.Threading.Tasks;

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
            CancellationToken cancellationToken = default);
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
