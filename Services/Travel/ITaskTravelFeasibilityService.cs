using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
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
            IReadOnlyList<ProposedRouteStop>? simulatedStops = null,
            long? planId = null);

        /// <summary>
        /// Returns travel feasibility for every technician for a proposed task slot.
        /// Zero writes. Returns selectable=true for all technicians when the address is unknown.
        /// </summary>
        Task<AssigneeTravelPreviewResponseDto> BuildAssigneeTravelPreviewAsync(
            DateTime targetDate,
            string objectAddress,
            int startMinutes,
            int endMinutes,
            int timezoneOffsetMinutes,
            long? excludeTaskId,
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
