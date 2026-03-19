using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.History
{
    /// <summary>
    /// Background service that deletes task action log entries older than 24 hours.
    /// Runs every hour so the table stays bounded without impacting write performance.
    /// </summary>
    public class TaskHistoryCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TaskHistoryCleanupService> _logger;

        private static readonly TimeSpan Interval   = TimeSpan.FromHours(1);
        private static readonly TimeSpan Retention  = TimeSpan.FromHours(24);

        public TaskHistoryCleanupService(IServiceScopeFactory scopeFactory, ILogger<TaskHistoryCleanupService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger       = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let the app fully start before the first run.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            _logger.LogInformation(
                "history.cleanup.started interval={IntervalHours}h retention={RetentionHours}h",
                Interval.TotalHours,
                Retention.TotalHours);

            await RunPruneCycleAsync(stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunPruneCycleAsync(stoppingToken);
            }
        }

        private async Task RunPruneCycleAsync(CancellationToken stoppingToken)
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "history.cleanup.error");
            }
        }

        private async Task PruneAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();

            var cutoff = DateTime.UtcNow - Retention;

            var deleted = await db.TaskActionLogs
                .Where(l => l.OccurredAtUtc < cutoff)
                .ExecuteDeleteAsync(cancellationToken);

            _logger.LogInformation("history.cleanup.completed count={Count} cutoff={Cutoff:O}", deleted, cutoff);
        }
    }
}
