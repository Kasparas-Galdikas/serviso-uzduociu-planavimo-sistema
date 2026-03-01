using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Background service that automatically expires tasks to Canceled status
    /// after their end time has passed without technician action.
    ///
    /// Tasks are auto-expired if:
    /// - Status is InProgress (technician hasn't updated it)
    /// - EndAt time has passed (in UTC)
    ///
    /// This ensures the workload calculation reflects reality - tasks that weren't
    /// marked as Completed by the technician are assumed to not have been done.
    /// </summary>
    public class TaskAutoExpirationService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TaskAutoExpirationService> _logger;

        // Run every 5 minutes to catch expired tasks reasonably quickly
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        // Grace period after task end time before auto-expiring (gives technician time to update)
        private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(30);

        public TaskAutoExpirationService(IServiceScopeFactory scopeFactory, ILogger<TaskAutoExpirationService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Wait a bit before first run to let the app fully start
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ExpireTasksAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TaskAutoExpirationService encountered an error.");
                }
            }
        }

        private async Task ExpireTasksAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
            var evaluator = scope.ServiceProvider.GetRequiredService<IEvaluationEngine>();
            var timeService = scope.ServiceProvider.GetRequiredService<IScheduleTimeService>();

            var cutoffTime = timeService.GetCurrentUtcTime() - GracePeriod;

            // Find all InProgress tasks that have ended (with grace period)
            var expiredTasks = await db.Tasks
                .Where(t => t.Status == ScheduleTaskStatus.InProgress && t.EndAt < cutoffTime)
                .ToListAsync(cancellationToken);

            if (expiredTasks.Count == 0)
            {
                return;
            }

            _logger.LogInformation(
                "svc.task.auto-expire.found count={Count} cutoff={Cutoff:O}",
                expiredTasks.Count, cutoffTime);

            // Group by plan for batch evaluation recalculation
            var planIds = expiredTasks
                .Where(t => t.PlanId.HasValue)
                .Select(t => t.PlanId!.Value)
                .Distinct()
                .ToList();

            // Update status to Canceled
            foreach (var task in expiredTasks)
            {
                task.Status = ScheduleTaskStatus.Canceled;

                _logger.LogInformation(
                    "svc.task.auto-expire.task taskId={TaskId} endAt={EndAt:O} planId={PlanId}",
                    task.Id, task.EndAt, task.PlanId);
            }

            await db.SaveChangesAsync(cancellationToken);

            // Recalculate evaluations for affected plans
            foreach (var planId in planIds)
            {
                try
                {
                    await evaluator.RecalculatePlanAsync(planId, cancellationToken);
                    _logger.LogInformation("svc.task.auto-expire.recalc planId={PlanId}", planId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to recalculate evaluations for plan {PlanId} after auto-expiration.", planId);
                }
            }

            _logger.LogInformation(
                "svc.task.auto-expire.complete expired={Count} plans={PlanCount}",
                expiredTasks.Count, planIds.Count);
        }
    }
}
