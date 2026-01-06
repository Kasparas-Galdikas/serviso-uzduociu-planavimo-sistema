using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.Evaluation
{
    public class EvaluationRepairService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<EvaluationRepairService> _logger;
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

        public EvaluationRepairService(IServiceScopeFactory scopeFactory, ILogger<EvaluationRepairService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RepairAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "EvaluationRepairService encountered an error.");
                }
            }
        }

        private async Task RepairAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
            var evaluator = scope.ServiceProvider.GetRequiredService<IEvaluationEngine>();

            var planTaskCounts = await db.Tasks
                .AsNoTracking()
                .Where(t => t.PlanId != null)
                .GroupBy(t => t.PlanId!.Value)
                .Select(g => new { PlanId = g.Key, TaskCount = g.Count() })
                .ToListAsync(cancellationToken);

            foreach (var plan in planTaskCounts)
            {
                var evalCount = await db.TaskEvaluations
                    .AsNoTracking()
                    .CountAsync(e => db.Tasks.Where(t => t.PlanId == plan.PlanId).Select(t => t.Id).Contains(e.TaskId), cancellationToken);

                var statusCount = await db.TaskEvaluations
                    .AsNoTracking()
                    .CountAsync(e => db.Tasks.Where(t => t.PlanId == plan.PlanId).Select(t => t.Id).Contains(e.TaskId)
                                             && e.Status != null, cancellationToken);

                if (plan.TaskCount > 0 && (evalCount < plan.TaskCount || statusCount < plan.TaskCount))
                {
                    try
                    {
                        await evaluator.RecalculatePlanAsync(plan.PlanId, cancellationToken);
                        _logger.LogInformation("svc.eval.repair planId={PlanId} tasks={Tasks} evals={Evals} status={StatusCount}", plan.PlanId, plan.TaskCount, evalCount, statusCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to repair evaluations for plan {PlanId}.", plan.PlanId);
                    }
                }
            }
        }
    }
}
