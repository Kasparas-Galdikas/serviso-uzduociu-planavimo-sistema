using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.BackgroundServices
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

        // Poll every minute. The partial index ix_tasks_inprogress_end_at makes each
        // tick an index scan over a tiny set (only active tasks), so the DB cost is
        // negligible even at 1-minute frequency.
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

        // No grace period — the edit-lock (IsTaskStillEditable) already prevents any
        // status change after EndAt, so a grace window only creates a gap where the
        // task is locked in the UI but still shows InProgress.
        private static readonly TimeSpan GracePeriod = TimeSpan.Zero;

        // How far back to look. Prevents the query from scanning unbounded history.
        // Any InProgress task older than this was either already expired or is a data anomaly.
        private static readonly TimeSpan LookbackWindow = TimeSpan.FromDays(2);

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
            var windowStart = cutoffTime - LookbackWindow;

            // Step 1: collect distinct plan IDs before mutating — lean projection, no tracking.
            // The partial index ix_tasks_inprogress_end_at covers this predicate directly.
            var planIds = await db.Tasks
                .Where(t => t.Status == ScheduleTaskStatus.InProgress
                         && t.EndAt > windowStart
                         && t.EndAt < cutoffTime)
                .Select(t => t.PlanId)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);

            // Step 2: single-statement bulk UPDATE — bypasses the change tracker entirely.
            // Returns the number of rows actually updated.
            var expired = await db.Tasks
                .Where(t => t.Status == ScheduleTaskStatus.InProgress
                         && t.EndAt > windowStart
                         && t.EndAt < cutoffTime)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.Status, ScheduleTaskStatus.Canceled),
                    cancellationToken);

            if (expired == 0)
            {
                return;
            }

            _logger.LogInformation(
                "svc.task.auto-expire.complete expired={Count} plans={PlanCount} cutoff={Cutoff:O}",
                expired, planIds.Count, cutoffTime);

            // Step 3: recalculate evaluations for affected plans
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
        }
    }
}
