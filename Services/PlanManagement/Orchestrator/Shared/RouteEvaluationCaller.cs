using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared
{
    internal class RouteEvaluationCaller
    {
        private readonly IRouteEvaluationService _routeEvaluationService;
        private readonly ILogger<RouteEvaluationCaller> _logger;

        public RouteEvaluationCaller(
            IRouteEvaluationService routeEvaluationService,
            ILogger<RouteEvaluationCaller> logger)
        {
            _routeEvaluationService = routeEvaluationService;
            _logger = logger;
        }

        internal async Task TriggerRouteEvaluationSafelyAsync(long technicianId, DateTime day, string action, CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "route.evaluation.triggered action={Action} technicianId={TechnicianId} day={Day:yyyy-MM-dd}",
                action, technicianId, day);
            try
            {
                await _routeEvaluationService.EvaluateTechnicianDayAsync(technicianId, day, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "route.evaluation.trigger.failed action={Action} technicianId={TechnicianId} day={Day:yyyy-MM-dd}",
                    action, technicianId, day);
            }
        }

        internal async Task TriggerTaskRouteImpactSafelyAsync(long taskId, string action, CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "route.evaluation.task-impact.triggered action={Action} taskId={TaskId}",
                action, taskId);
            try
            {
                await _routeEvaluationService.EvaluateTaskRouteImpactAsync(taskId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "route.evaluation.task-impact.trigger.failed action={Action} taskId={TaskId}",
                    action, taskId);
            }
        }
    }
}
