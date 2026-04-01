using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared
{
    internal class RouteEvaluationCaller
    {
        private const int MaxParallelRouteEvaluations = 4;
        private readonly IRouteEvaluationService _routeEvaluationService;
        private readonly IServiceScopeFactory? _scopeFactory;
        private readonly ILogger<RouteEvaluationCaller> _logger;

        public RouteEvaluationCaller(
            IRouteEvaluationService routeEvaluationService,
            ILogger<RouteEvaluationCaller> logger)
            : this(routeEvaluationService, scopeFactory: null, logger)
        {
        }

        public RouteEvaluationCaller(
            IRouteEvaluationService routeEvaluationService,
            IServiceScopeFactory? scopeFactory,
            ILogger<RouteEvaluationCaller> logger)
        {
            _routeEvaluationService = routeEvaluationService;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        internal async Task TriggerRouteEvaluationSafelyAsync(long technicianId, DateTime day, string action, CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "route.evaluation.triggered action={Action} technicianId={TechnicianId} day={Day:yyyy-MM-dd}",
                action, technicianId, day);
            try
            {
                await EvaluateTechnicianDayAsync(technicianId, day, recalculateEvaluations: true, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "route.evaluation.trigger.failed action={Action} technicianId={TechnicianId} day={Day:yyyy-MM-dd}",
                    action, technicianId, day);
            }
        }

        internal async Task TriggerRouteEvaluationBatchSafelyAsync(
            IEnumerable<(long TechnicianId, DateTime Day)> targets,
            string action,
            bool recalculateEvaluations,
            CancellationToken cancellationToken)
        {
            var distinctTargets = targets
                .Select(t => (t.TechnicianId, t.Day.Date))
                .Distinct()
                .ToList();

            if (distinctTargets.Count == 0)
            {
                return;
            }

            _logger.LogInformation(
                "route.evaluation.batch.triggered action={Action} targetCount={TargetCount}",
                action,
                distinctTargets.Count);

            if (_scopeFactory == null)
            {
                foreach (var target in distinctTargets)
                {
                    await TriggerRouteEvaluationSafelyAsync(target.TechnicianId, target.Date, action, recalculateEvaluations, cancellationToken);
                }

                return;
            }

            using var gate = new SemaphoreSlim(MaxParallelRouteEvaluations);
            var tasks = distinctTargets.Select(async target =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    await TriggerRouteEvaluationSafelyAsync(target.TechnicianId, target.Date, action, recalculateEvaluations, cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        private async Task TriggerRouteEvaluationSafelyAsync(
            long technicianId,
            DateTime day,
            string action,
            bool recalculateEvaluations,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "route.evaluation.triggered action={Action} technicianId={TechnicianId} day={Day:yyyy-MM-dd}",
                action, technicianId, day);
            try
            {
                await EvaluateTechnicianDayAsync(technicianId, day, recalculateEvaluations, cancellationToken);
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

        private async Task EvaluateTechnicianDayAsync(long technicianId, DateTime day, bool recalculateEvaluations, CancellationToken cancellationToken)
        {
            if (_scopeFactory == null)
            {
                if (recalculateEvaluations)
                {
                    await _routeEvaluationService.EvaluateTechnicianDayAsync(technicianId, day, cancellationToken);
                }
                else
                {
                    await _routeEvaluationService.EvaluateTechnicianDayRoutesOnlyAsync(technicianId, day, cancellationToken);
                }
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var scopedRouteEvaluationService = scope.ServiceProvider.GetRequiredService<IRouteEvaluationService>();
            if (recalculateEvaluations)
            {
                await scopedRouteEvaluationService.EvaluateTechnicianDayAsync(technicianId, day, cancellationToken);
            }
            else
            {
                await scopedRouteEvaluationService.EvaluateTechnicianDayRoutesOnlyAsync(technicianId, day, cancellationToken);
            }
        }
    }
}
