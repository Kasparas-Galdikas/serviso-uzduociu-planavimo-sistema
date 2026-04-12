using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation
{
    public interface IRouteEvaluationService
    {
        Task EvaluateTaskRouteImpactAsync(long taskId, CancellationToken cancellationToken = default);
        Task EvaluateTechnicianDayAsync(long technicianId, DateTime dayUtc, CancellationToken cancellationToken = default);
        Task EvaluateTechnicianDayRoutesOnlyAsync(long technicianId, DateTime dayUtc, CancellationToken cancellationToken = default);
        Task EvaluatePlanDayAsync(long planId, DateTime dayUtc, CancellationToken cancellationToken = default);
        Task EnsureAllPairwiseRoutesAsync(IEnumerable<long> addressIds, CancellationToken cancellationToken = default);
        Task<int> RefreshStaleRoutesAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);

        /// <summary>
        /// One-time startup pass. Phase A: geocodes addresses with no coordinates.
        /// Phase B: snaps all geocoded-but-unsnapped addresses to the road network in batches.
        /// Stops gracefully if the daily Snap quota is exhausted (HTTP 429).
        /// </summary>
        Task BackfillUnsnappedAddressesAsync(CancellationToken cancellationToken = default);
    }
}
