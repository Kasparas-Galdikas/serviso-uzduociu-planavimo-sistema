using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement;

namespace ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation
{
    public class RouteEvaluationService : IRouteEvaluationService
    {
        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly ILogger<RouteEvaluationService> _logger;
        private readonly ILocationApiClient _locationApiClient;
        private readonly IDistanceApiClient _distanceApiClient;
        private readonly string _heigitApiKey;

        public RouteEvaluationService(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            ILogger<RouteEvaluationService> logger,
            IConfiguration configuration,
            ILocationApiClient locationApiClient,
            IDistanceApiClient distanceApiClient)
        {
            _db = db;
            _evaluationEngine = evaluationEngine;
            _logger = logger;
            _locationApiClient = locationApiClient;
            _distanceApiClient = distanceApiClient;
            _heigitApiKey = configuration["HEIGIT_API_KEY"] ?? string.Empty;
        }

        public async Task EvaluateTaskRouteImpactAsync(long taskId, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured(taskId))
            {
                return;
            }

            var task = await _db.Tasks
                .Include(t => t.Technician)
                    .ThenInclude(tech => tech.WorkplaceAddress)
                .Include(t => t.ServiceObject)
                    .ThenInclude(o => o.Address)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);
            if (task == null)
            {
                _logger.LogWarning("route.evaluation.task-impact.skipped taskId={TaskId} reason=not-found", taskId);
                return;
            }

            if (task.Technician?.WorkplaceAddress == null || task.ServiceObject?.Address == null)
            {
                _logger.LogWarning("route.evaluation.task-impact.skipped taskId={TaskId} reason=missing-address", taskId);
                return;
            }

            _logger.LogInformation("route.evaluation.task-impact.started taskId={TaskId}", taskId);

            var dayStart = task.StartAt.Date;
            var dayEnd = dayStart.AddDays(1);
            var dayTasks = await _db.Tasks
                .Include(t => t.ServiceObject)
                    .ThenInclude(o => o.Address)
                .Where(t => t.TechnicianId == task.TechnicianId)
                .Where(t => t.StartAt >= dayStart && t.StartAt < dayEnd)
                .Where(t => task.PlanId == null ? t.PlanId == null : t.PlanId == task.PlanId)
                .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                .OrderBy(t => t.StartAt)
                .ToListAsync(cancellationToken);

            if (dayTasks.Count == 0)
            {
                _logger.LogInformation("route.evaluation.task-impact.skipped taskId={TaskId} reason=no-day-tasks", taskId);
                return;
            }

            var routeAddresses = new List<AddressEntity> { task.Technician.WorkplaceAddress };
            routeAddresses.AddRange(dayTasks.Select(t => t.ServiceObject.Address));

            _logger.LogInformation(
                "route.evaluation.task-impact.address-chain taskId={TaskId} technicianId={TechnicianId} chain=[{Chain}]",
                taskId,
                task.TechnicianId,
                string.Join(" → ", routeAddresses.Select(a => a.FullAddress)));

            await EnsureCoordinatesAsync(routeAddresses, cancellationToken);

            var missingPairs = await GetMissingSequentialPairsAsync(routeAddresses, cancellationToken);
            if (missingPairs.Count == 0)
            {
                _logger.LogInformation("route.evaluation.task-impact.skipped taskId={TaskId} reason=routes-already-exist", taskId);
                if (task.PlanId.HasValue)
                {
                    await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
                }
                else
                {
                    await _evaluationEngine.RecalculateTasksAsync(dayTasks.Select(t => t.Id), cancellationToken);
                }
                return;
            }

            var points = routeAddresses
                .Select(a => new GeoPoint(a.Latitude ?? 0m, a.Longitude ?? 0m))
                .ToList();
            var matrix = await _distanceApiClient.GetMatrixAsync(points, cancellationToken);

            await UpsertSequentialRoutesAsync(routeAddresses, matrix, missingPairs, cancellationToken);

            if (task.PlanId.HasValue)
            {
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
            }
            else
            {
                await _evaluationEngine.RecalculateTasksAsync(dayTasks.Select(t => t.Id), cancellationToken);
            }

            _logger.LogInformation("route.evaluation.task-impact.completed taskId={TaskId}", taskId);
        }

        public async Task EvaluateTechnicianDayAsync(long technicianId, DateTime dayUtc, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured())
            {
                return;
            }

            _logger.LogInformation(
                "route.evaluation.technician-day.started technicianId={TechnicianId} day={Day}",
                technicianId,
                dayUtc.Date);

            var dayTasks = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.TechnicianId == technicianId)
                .Where(t => t.StartAt >= dayUtc.Date && t.StartAt < dayUtc.Date.AddDays(1))
                .OrderBy(t => t.StartAt)
                .Select(t => new { t.Id, t.StartAt })
                .ToListAsync(cancellationToken);

            _logger.LogInformation(
                "route.evaluation.technician-day.chain technicianId={TechnicianId} day={Day} chain=[{Chain}]",
                technicianId,
                dayUtc.Date,
                string.Join(" → ", dayTasks.Select(t => $"{t.Id}@{t.StartAt:HH:mm}")));

            foreach (var t in dayTasks)
            {
                await EvaluateTaskRouteImpactAsync(t.Id, cancellationToken);
            }

            _logger.LogInformation(
                "route.evaluation.technician-day.completed technicianId={TechnicianId} day={Day} tasks={TaskCount}",
                technicianId,
                dayUtc.Date,
                dayTasks.Count);
        }

        public async Task EvaluatePlanDayAsync(long planId, DateTime dayUtc, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured())
            {
                return;
            }

            _logger.LogInformation(
                "route.evaluation.plan-day.started planId={PlanId} day={Day}",
                planId,
                dayUtc.Date);

            var technicianIds = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.PlanId == planId)
                .Where(t => t.StartAt >= dayUtc.Date && t.StartAt < dayUtc.Date.AddDays(1))
                .Select(t => t.TechnicianId)
                .Distinct()
                .ToListAsync(cancellationToken);

            foreach (var technicianId in technicianIds)
            {
                await EvaluateTechnicianDayAsync(technicianId, dayUtc.Date, cancellationToken);
            }
        }

        private bool IsConfigured(long? taskId = null)
        {
            if (!string.IsNullOrWhiteSpace(_heigitApiKey) && _heigitApiKey != "__PLACEHOLDER__")
            {
                return true;
            }

            if (taskId.HasValue)
            {
                _logger.LogWarning("route.evaluation.config-missing key=HEIGIT_API_KEY taskId={TaskId}", taskId.Value);
            }
            else
            {
                _logger.LogWarning("route.evaluation.config-missing key=HEIGIT_API_KEY");
            }

            return false;
        }

        private async Task EnsureCoordinatesAsync(IEnumerable<AddressEntity> addresses, CancellationToken cancellationToken)
        {
            foreach (var address in addresses
                .GroupBy(a => a.Id)
                .Select(g => g.First()))
            {
                if (address.Latitude.HasValue && address.Longitude.HasValue)
                {
                    continue;
                }

                var coords = await _locationApiClient.GeocodeAsync(address.FullAddress, cancellationToken);
                address.Latitude = coords.Latitude;
                address.Longitude = coords.Longitude;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        private async Task UpsertSequentialRoutesAsync(
            IReadOnlyList<AddressEntity> routeAddresses,
            MatrixResult matrix,
            IReadOnlyList<(long FromAddressId, long ToAddressId, int MatrixFrom, int MatrixTo)> pairsToInsert,
            CancellationToken cancellationToken)
        {
            if (routeAddresses.Count < 2 || pairsToInsert.Count == 0)
            {
                return;
            }

            foreach (var pair in pairsToInsert)
            {
                var distanceMeters = matrix.DistancesMeters[pair.MatrixFrom][pair.MatrixTo];
                var durationSeconds = matrix.DurationsSeconds[pair.MatrixFrom][pair.MatrixTo];
                var distanceKm = Math.Round((decimal)(distanceMeters / 1000d), 2);
                var durationMinutes = (int)Math.Ceiling(durationSeconds / 60d);

                _db.TravelRoutes.Add(new TravelRouteEntity
                {
                    FromAddressId = pair.FromAddressId,
                    ToAddressId = pair.ToAddressId,
                    DistanceKm = distanceKm,
                    DurationMinutes = durationMinutes,
                    CalculatedAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        private async Task<List<(long FromAddressId, long ToAddressId, int MatrixFrom, int MatrixTo)>> GetMissingSequentialPairsAsync(
            IReadOnlyList<AddressEntity> routeAddresses,
            CancellationToken cancellationToken)
        {
            var requiredPairs = Enumerable.Range(0, routeAddresses.Count - 1)
                .Select(i => (FromAddressId: routeAddresses[i].Id, ToAddressId: routeAddresses[i + 1].Id, MatrixFrom: i, MatrixTo: i + 1))
                .Distinct()
                .ToList();

            if (requiredPairs.Count == 0)
            {
                return new List<(long FromAddressId, long ToAddressId, int MatrixFrom, int MatrixTo)>();
            }

            var fromIds = requiredPairs.Select(p => p.FromAddressId).Distinct().ToList();
            var toIds = requiredPairs.Select(p => p.ToAddressId).Distinct().ToList();

            var existingPairs = await _db.TravelRoutes
                .AsNoTracking()
                .Where(r => fromIds.Contains(r.FromAddressId) && toIds.Contains(r.ToAddressId))
                .Select(r => new { r.FromAddressId, r.ToAddressId })
                .ToListAsync(cancellationToken);

            var existingLookup = existingPairs
                .Select(p => (p.FromAddressId, p.ToAddressId))
                .ToHashSet();

            return requiredPairs
                .Where(p => !existingLookup.Contains((p.FromAddressId, p.ToAddressId)))
                .ToList();
        }
    }
}
