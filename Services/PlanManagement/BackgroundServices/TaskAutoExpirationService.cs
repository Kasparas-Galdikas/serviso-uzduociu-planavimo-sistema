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

        public TaskAutoExpirationService(IServiceScopeFactory scopeFactory, ILogger<TaskAutoExpirationService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Wait a bit before first run to let the app fully start
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

            var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ExpireTasksAsync(stoppingToken);
                    await CleanupExpiredUnavailabilityAsync(stoppingToken);
                    await CleanupExpiredDraftTasksAsync(stoppingToken);
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

            // Step 1: collect distinct plan IDs before mutating — lean projection, no tracking.
            // The partial index ix_tasks_inprogress_end_at covers this predicate directly.
            var planIds = await db.Tasks
                .Where(t => t.Status == ScheduleTaskStatus.InProgress
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

        private async Task CleanupExpiredUnavailabilityAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
            var timeService = scope.ServiceProvider.GetRequiredService<IScheduleTimeService>();

            var cutoffTime = timeService.GetCurrentUtcTime();

            // Include records whose stored end might still need legacy normalization.
            var candidates = await db.TechnicianUnavailabilities
                .Where(u => u.PeriodEnd < cutoffTime.AddDays(1))
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0)
            {
                return;
            }

            var expiredIds = candidates
                .Where(u => u.PeriodEnd < cutoffTime)
                .Select(u => u.Id)
                .ToList();

            if (expiredIds.Count == 0)
            {
                return;
            }

            var deleted = await db.TechnicianUnavailabilities
                .Where(u => expiredIds.Contains(u.Id))
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "svc.unavailability.auto-cleanup.complete deleted={Count} cutoff={Cutoff:O}",
                    deleted,
                    cutoffTime);
            }
        }

        private async Task CleanupExpiredDraftTasksAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
            var timeService = scope.ServiceProvider.GetRequiredService<IScheduleTimeService>();
            var evaluator = scope.ServiceProvider.GetRequiredService<IEvaluationEngine>();

            var cutoffTime = timeService.GetCurrentUtcTime();

            var affectedPlanIds = await db.Tasks
                .Where(t => t.PlanId.HasValue
                    && t.Plan != null
                    && t.Plan.Status == PlanStatus.Draft
                    && t.EndAt < cutoffTime)
                .Select(t => t.PlanId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);

            if (affectedPlanIds.Count == 0)
            {
                return;
            }

            var deleted = await db.Tasks
                .Where(t => t.PlanId.HasValue
                    && t.Plan != null
                    && t.Plan.Status == PlanStatus.Draft
                    && t.EndAt < cutoffTime)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted == 0)
            {
                return;
            }

            foreach (var planId in affectedPlanIds)
            {
                try
                {
                    await evaluator.RecalculatePlanAsync(planId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to recalculate draft plan {PlanId} after expired draft-task cleanup.", planId);
                }
            }

            _logger.LogInformation(
                "svc.plan.draft.auto-cleanup.complete deleted={Count} plans={PlanCount} cutoff={Cutoff:O}",
                deleted,
                affectedPlanIds.Count,
                cutoffTime);
        }
    }
}
