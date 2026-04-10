using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.BackgroundServices
{
    public sealed class RouteRefreshBackgroundService : BackgroundService
    {
        private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan RouteMaxAge = TimeSpan.FromHours(24);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RouteRefreshBackgroundService> _logger;

        public RouteRefreshBackgroundService(
            IServiceScopeFactory scopeFactory,
            ILogger<RouteRefreshBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(InitialDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // One-time startup pass: geocode any missing addresses and snap all
            // geocoded-but-unsnapped addresses to the road network. Stops gracefully
            // if the daily Snap quota is exhausted.
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IRouteEvaluationService>();
                await svc.BackfillUnsnappedAddressesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RouteRefreshBackgroundService: snap backfill encountered an error.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var routeEvaluationService = scope.ServiceProvider.GetRequiredService<IRouteEvaluationService>();
                    await routeEvaluationService.RefreshStaleRoutesAsync(RouteMaxAge, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RouteRefreshBackgroundService encountered an error.");
                }

                try
                {
                    await Task.Delay(RefreshInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
