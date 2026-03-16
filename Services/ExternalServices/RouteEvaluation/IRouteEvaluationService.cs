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
        Task EvaluatePlanDayAsync(long planId, DateTime dayUtc, CancellationToken cancellationToken = default);
        Task EnsureAllPairwiseRoutesAsync(IEnumerable<long> addressIds, CancellationToken cancellationToken = default);
    }
}
