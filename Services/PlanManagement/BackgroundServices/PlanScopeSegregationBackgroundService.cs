using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.BackgroundServices
{
    public sealed class PlanScopeSegregationBackgroundService : BackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PlanScopeSegregationBackgroundService> _logger;

        public PlanScopeSegregationBackgroundService(
            IServiceScopeFactory scopeFactory,
            ILogger<PlanScopeSegregationBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(StartupDelay, stoppingToken);

            var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SegregateAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PlanScopeSegregationBackgroundService encountered an error.");
                }
            }
        }

        private async Task SegregateAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var segregationService = scope.ServiceProvider.GetRequiredService<PlanScopeSegregationService>();
            var evaluator = scope.ServiceProvider.GetRequiredService<IEvaluationEngine>();

            var result = await segregationService.EnsureActivePlanScopedAsync(cancellationToken);
            if (!result.WorkPerformed)
            {
                return;
            }

            var planIdsToRecalculate = new HashSet<long>(result.AffectedSupersededPlanIds);
            if (result.ActivePlanId.HasValue)
            {
                planIdsToRecalculate.Add(result.ActivePlanId.Value);
            }

            foreach (var planId in planIdsToRecalculate)
            {
                try
                {
                    await evaluator.RecalculatePlanAsync(planId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to recalculate evaluations for segregated plan {PlanId}.", planId);
                }
            }

            _logger.LogInformation(
                "svc.plan.scope-segregation.complete activePlanId={ActivePlanId} movedTasks={MovedTaskCount} createdSupersededPlans={CreatedPlanCount} reusedSupersededPlans={ReusedPlanCount} recalculatedPlans={RecalculatedPlanCount}",
                result.ActivePlanId,
                result.MovedTaskCount,
                result.CreatedSupersededPlanCount,
                result.ReusedSupersededPlanCount,
                planIdsToRecalculate.Count);
        }
    }
}
