using System.Threading;
using System.Threading.Tasks;
using System;

namespace ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation
{
    public interface IRouteEvaluationService
    {
        Task EvaluateTaskRouteImpactAsync(long taskId, CancellationToken cancellationToken = default);
        Task EvaluateTechnicianDayAsync(long technicianId, DateTime dayUtc, CancellationToken cancellationToken = default);
        Task EvaluatePlanDayAsync(long planId, DateTime dayUtc, CancellationToken cancellationToken = default);
    }
}
