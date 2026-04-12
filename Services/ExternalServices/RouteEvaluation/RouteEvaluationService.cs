using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;

namespace ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation
{
    public class RouteEvaluationService : IRouteEvaluationService
    {
        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly ILogger<RouteEvaluationService> _logger;
        private readonly ILocationApiClient _locationApiClient;
        private readonly IDistanceApiClient _distanceApiClient;
        private readonly ISnapApiClient _snapApiClient;
        private readonly IRouteChainBuilder _routeChainBuilder;
        private readonly IScheduleTimeService _timeService;
        private readonly string _heigitApiKey;

        public RouteEvaluationService(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            ILogger<RouteEvaluationService> logger,
            IConfiguration configuration,
            ILocationApiClient locationApiClient,
            IDistanceApiClient distanceApiClient,
            ISnapApiClient snapApiClient,
            IRouteChainBuilder routeChainBuilder,
            IScheduleTimeService timeService)
        {
            _db = db;
            _evaluationEngine = evaluationEngine;
            _logger = logger;
            _locationApiClient = locationApiClient;
            _distanceApiClient = distanceApiClient;
            _snapApiClient = snapApiClient;
            _routeChainBuilder = routeChainBuilder;
            _timeService = timeService;
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

            var chain = await _routeChainBuilder.BuildDayChainAsync(
                new RouteChainRequest
                {
                    TechnicianId = task.TechnicianId,
                    DayUtc = task.StartAt.Date,
                    IncludeCanceled = false,
                    FilterByPlanId = true,
                    PlanId = task.PlanId
                },
                cancellationToken);
            if (chain == null || chain.Stops.Count == 0)
            {
                _logger.LogInformation("route.evaluation.task-impact.skipped taskId={TaskId} reason=no-day-tasks", taskId);
                return;
            }

            var routeAddresses = await LoadAddressPathAsync(chain, cancellationToken);

            _logger.LogInformation(
                "route.evaluation.task-impact.address-chain taskId={TaskId} technicianId={TechnicianId} chain=[{Chain}]",
                taskId,
                task.TechnicianId,
                string.Join(" -> ", routeAddresses.Select(a => a.FullAddress)));

            await EnsureCoordinatesAsync(routeAddresses, cancellationToken);
            await EnsureSnappedCoordinatesAsync(routeAddresses, cancellationToken);
            if (HasMissingCoordinates(routeAddresses))
            {
                _logger.LogInformation("route.evaluation.task-impact.skipped taskId={TaskId} reason=missing-coordinates", taskId);
                return;
            }

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
                    await _evaluationEngine.RecalculateTasksAsync(chain.Stops.Where(s => s.TaskId.HasValue).Select(s => s.TaskId!.Value), cancellationToken);
                }
                return;
            }

            var points = routeAddresses
                .Select(a => new GeoPoint(
                    a.SnappedLatitude  ?? a.Latitude  ?? 0m,
                    a.SnappedLongitude ?? a.Longitude ?? 0m))
                .ToList();
            var matrix = await _distanceApiClient.GetMatrixAsync(points, cancellationToken);

            await UpsertSequentialRoutesAsync(routeAddresses, matrix, missingPairs, cancellationToken);

            if (task.PlanId.HasValue)
            {
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
            }
            else
            {
                await _evaluationEngine.RecalculateTasksAsync(chain.Stops.Where(s => s.TaskId.HasValue).Select(s => s.TaskId!.Value), cancellationToken);
            }

            _logger.LogInformation("route.evaluation.task-impact.completed taskId={TaskId}", taskId);
        }

        public async Task EvaluateTechnicianDayAsync(long technicianId, DateTime dayUtc, CancellationToken cancellationToken = default)
        {
            await EvaluateTechnicianDayCoreAsync(technicianId, dayUtc, recalculateEvaluations: true, cancellationToken);
        }

        public async Task EvaluateTechnicianDayRoutesOnlyAsync(long technicianId, DateTime dayUtc, CancellationToken cancellationToken = default)
        {
            await EvaluateTechnicianDayCoreAsync(technicianId, dayUtc, recalculateEvaluations: false, cancellationToken);
        }

        private async Task EvaluateTechnicianDayCoreAsync(
            long technicianId,
            DateTime dayUtc,
            bool recalculateEvaluations,
            CancellationToken cancellationToken)
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
                .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                .OrderBy(t => t.StartAt)
                .Select(t => new { t.Id, t.StartAt, t.PlanId })
                .ToListAsync(cancellationToken);

            _logger.LogInformation(
                "route.evaluation.technician-day.chain technicianId={TechnicianId} day={Day} chain=[{Chain}]",
                technicianId,
                dayUtc.Date,
                string.Join(" -> ", dayTasks.Select(t => $"{t.Id}@{t.StartAt:HH:mm}UTC")));

            if (dayTasks.Count == 0)
            {
                _logger.LogInformation(
                    "route.evaluation.technician-day.completed technicianId={TechnicianId} day={Day} tasks=0",
                    technicianId,
                    dayUtc.Date);
                return;
            }

            var planIds = dayTasks
                .Select(t => t.PlanId)
                .Distinct()
                .ToList();

            foreach (var planId in planIds)
            {
                var chain = await _routeChainBuilder.BuildDayChainAsync(
                    new RouteChainRequest
                    {
                        TechnicianId = technicianId,
                        DayUtc = dayUtc.Date,
                        IncludeCanceled = false,
                        FilterByPlanId = true,
                        PlanId = planId
                    },
                    cancellationToken);
                if (chain == null || chain.Stops.Count == 0)
                {
                    continue;
                }

                var routeAddresses = await LoadAddressPathAsync(chain, cancellationToken);

                await EnsureCoordinatesAsync(routeAddresses, cancellationToken);
                await EnsureSnappedCoordinatesAsync(routeAddresses, cancellationToken);
                if (HasMissingCoordinates(routeAddresses))
                {
                    _logger.LogInformation(
                        "route.evaluation.technician-day.skipped technicianId={TechnicianId} day={Day} planId={PlanId} reason=missing-coordinates",
                        technicianId,
                        dayUtc.Date,
                        planId);
                    continue;
                }

                var missingPairs = await GetMissingSequentialPairsAsync(routeAddresses, cancellationToken);
                if (missingPairs.Count == 0)
                {
                    continue;
                }

                var points = routeAddresses
                    .Select(a => new GeoPoint(
                        a.SnappedLatitude  ?? a.Latitude  ?? 0m,
                        a.SnappedLongitude ?? a.Longitude ?? 0m))
                    .ToList();
                var matrix = await _distanceApiClient.GetMatrixAsync(points, cancellationToken);
                await UpsertSequentialRoutesAsync(routeAddresses, matrix, missingPairs, cancellationToken);
            }

            if (recalculateEvaluations)
            {
                var affectedPlanIds = planIds
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToList();
                foreach (var planId in affectedPlanIds)
                {
                    await _evaluationEngine.RecalculatePlanAsync(planId, cancellationToken);
                }

                var unplannedTaskIds = dayTasks
                    .Where(t => !t.PlanId.HasValue)
                    .Select(t => t.Id)
                    .Distinct()
                    .ToList();
                if (unplannedTaskIds.Count > 0)
                {
                    await _evaluationEngine.RecalculateTasksAsync(unplannedTaskIds, cancellationToken);
                }
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

        private async Task<List<AddressEntity>> LoadAddressPathAsync(RouteChain chain, CancellationToken cancellationToken)
        {
            var addressPathIds = chain.BuildAddressPathIds();
            var addressLookup = await _db.Addresses
                .Where(a => addressPathIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, cancellationToken);

            return addressPathIds
                .Where(id => addressLookup.ContainsKey(id))
                .Select(id => addressLookup[id])
                .ToList();
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

        private async Task EnsureSnappedCoordinatesAsync(IEnumerable<AddressEntity> addresses, CancellationToken cancellationToken)
        {
            var toSnap = addresses
                .GroupBy(a => a.Id)
                .Select(g => g.First())
                .Where(a => a.Latitude.HasValue && a.Longitude.HasValue && !a.SnappedLatitude.HasValue)
                .ToList();

            if (toSnap.Count == 0)
            {
                return;
            }

            var points = toSnap
                .Select(a => new GeoPoint(a.Latitude!.Value, a.Longitude!.Value))
                .ToList();

            try
            {
                var snapped = await _snapApiClient.SnapAsync(points, cancellationToken: cancellationToken);

                for (var i = 0; i < toSnap.Count; i++)
                {
                    if (snapped[i] is { } pt)
                    {
                        toSnap[i].SnappedLatitude  = pt.Latitude;
                        toSnap[i].SnappedLongitude = pt.Longitude;
                    }
                    else
                    {
                        // No road within snap radius — fall back to geocoded coords so
                        // we don't retry snap on every evaluation for this address.
                        toSnap[i].SnappedLatitude  = toSnap[i].Latitude;
                        toSnap[i].SnappedLongitude = toSnap[i].Longitude;
                        _logger.LogInformation(
                            "route.snap.fallback addressId={AddressId}",
                            toSnap[i].Id);
                    }
                }

                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "route.snap.failed count={Count} — will use raw geocoded coords for matrix",
                    toSnap.Count);
                // Non-fatal: matrix will proceed with raw geocoded coords via SnappedLat ?? Lat fallback
            }
        }

        public async Task BackfillUnsnappedAddressesAsync(CancellationToken cancellationToken = default)
        {
            const int SnapBatchSize = 50;
            const int SnapRadius    = 500;

            if (!IsConfigured())
            {
                return;
            }

            _logger.LogInformation("route.snap.backfill.start");

            // ── Phase A: geocode addresses that have no coordinates at all ──────────────
            var ungeocoded = await _db.Addresses
                .Where(a => a.Latitude == null)
                .OrderBy(a => a.Id)
                .ToListAsync(cancellationToken);

            _logger.LogInformation(
                "route.snap.backfill.phase-a.start count={Count}",
                ungeocoded.Count);

            var geocodedCount     = 0;
            var geocodeFailCount  = 0;

            foreach (var address in ungeocoded)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    var coords = await _locationApiClient.GeocodeAsync(address.FullAddress, cancellationToken);
                    address.Latitude  = coords.Latitude;
                    address.Longitude = coords.Longitude;
                    await _db.SaveChangesAsync(cancellationToken);
                    geocodedCount++;
                    _logger.LogInformation(
                        "route.snap.backfill.geocoded addressId={AddressId}",
                        address.Id);
                }
                catch (Exception ex)
                {
                    geocodeFailCount++;
                    _logger.LogWarning(
                        ex,
                        "route.snap.backfill.geocode-failed addressId={AddressId} address={Address}",
                        address.Id,
                        address.FullAddress);
                }
            }

            _logger.LogInformation(
                "route.snap.backfill.phase-a.done geocoded={Geocoded} failed={Failed}",
                geocodedCount,
                geocodeFailCount);

            // ── Phase B: snap all geocoded-but-unsnapped addresses ───────────────────
            var toSnap = await _db.Addresses
                .Where(a => a.Latitude != null && a.SnappedLatitude == null)
                .OrderBy(a => a.Id)
                .ToListAsync(cancellationToken);

            _logger.LogInformation(
                "route.snap.backfill.phase-b.start count={Count}",
                toSnap.Count);

            var snappedCount   = 0;
            var fallbackCount  = 0;
            var processedCount = 0;

            var batches = toSnap
                .Select((a, i) => new { a, i })
                .GroupBy(x => x.i / SnapBatchSize)
                .Select(g => g.Select(x => x.a).ToList())
                .ToList();

            foreach (var batch in batches)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var points = batch
                    .Select(a => new GeoPoint(a.Latitude!.Value, a.Longitude!.Value))
                    .ToList();

                try
                {
                    var snapped = await _snapApiClient.SnapAsync(points, SnapRadius, cancellationToken);

                    for (var i = 0; i < batch.Count; i++)
                    {
                        if (snapped[i] is { } pt)
                        {
                            batch[i].SnappedLatitude  = pt.Latitude;
                            batch[i].SnappedLongitude = pt.Longitude;
                            snappedCount++;
                        }
                        else
                        {
                            batch[i].SnappedLatitude  = batch[i].Latitude;
                            batch[i].SnappedLongitude = batch[i].Longitude;
                            fallbackCount++;
                            _logger.LogInformation(
                                "route.snap.backfill.fallback addressId={AddressId}",
                                batch[i].Id);
                        }
                    }

                    await _db.SaveChangesAsync(cancellationToken);
                    processedCount += batch.Count;

                    _logger.LogInformation(
                        "route.snap.backfill.batch.done batchSize={BatchSize} processedTotal={Processed} snapped={Snapped} fallback={Fallback} remaining={Remaining}",
                        batch.Count,
                        processedCount,
                        snappedCount,
                        fallbackCount,
                        toSnap.Count - processedCount);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning(
                        "route.snap.backfill.quota-hit remaining={Remaining} snapped={Snapped} fallback={Fallback} — stopping backfill; unsnapped addresses will be handled on-demand",
                        toSnap.Count - processedCount,
                        snappedCount,
                        fallbackCount);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "route.snap.backfill.batch.failed batchSize={BatchSize} processedTotal={Processed} — skipping batch",
                        batch.Count,
                        processedCount);
                    // Continue to next batch
                }
            }

            _logger.LogInformation(
                "route.snap.backfill.complete snapped={Snapped} fallback={Fallback} geocoded={Geocoded} geocodeFailed={GeocodeFailed}",
                snappedCount,
                fallbackCount,
                geocodedCount,
                geocodeFailCount);
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

                try
                {
                    var coords = await _locationApiClient.GeocodeAsync(address.FullAddress, cancellationToken);
                    address.Latitude = coords.Latitude;
                    address.Longitude = coords.Longitude;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "route.evaluation.geocode.failed addressId={AddressId} address={Address}",
                        address.Id,
                        address.FullAddress);
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        private static bool HasMissingCoordinates(IEnumerable<AddressEntity> addresses)
        {
            return addresses.Any(address => !address.Latitude.HasValue || !address.Longitude.HasValue);
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
                var calculatedAt = _timeService.GetCurrentUtcTime();

                if (_db.Database.IsRelational())
                {
                    // Keep route cache writes race-safe: warmup/evaluation can run concurrently.
                    await _db.Database.ExecuteSqlInterpolatedAsync(
                        $@"INSERT INTO travel_routes (from_address_id, to_address_id, distance_km, duration_minutes, calculated_at)
                           VALUES ({pair.FromAddressId}, {pair.ToAddressId}, {distanceKm}, {durationMinutes}, {calculatedAt})
                           ON CONFLICT (from_address_id, to_address_id)
                           DO UPDATE SET
                               distance_km = EXCLUDED.distance_km,
                               duration_minutes = EXCLUDED.duration_minutes,
                               calculated_at = EXCLUDED.calculated_at",
                        cancellationToken);
                }
                else
                {
                    // Unit tests use InMemory provider (no SQL/ON CONFLICT support).
                    var existing = await _db.TravelRoutes
                        .FirstOrDefaultAsync(
                            r => r.FromAddressId == pair.FromAddressId && r.ToAddressId == pair.ToAddressId,
                            cancellationToken);
                    if (existing == null)
                    {
                        _db.TravelRoutes.Add(new TravelRouteEntity
                        {
                            FromAddressId = pair.FromAddressId,
                            ToAddressId = pair.ToAddressId,
                            DistanceKm = distanceKm,
                            DurationMinutes = durationMinutes,
                            CalculatedAt = calculatedAt
                        });
                    }
                    else
                    {
                        existing.DistanceKm = distanceKm;
                        existing.DurationMinutes = durationMinutes;
                        existing.CalculatedAt = calculatedAt;
                    }

                    await _db.SaveChangesAsync(cancellationToken);
                }
            }
        }

        public async Task EnsureAllPairwiseRoutesAsync(IEnumerable<long> addressIds, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured())
                return;

            var addressIdList = addressIds.Distinct().ToList();
            if (addressIdList.Count < 2)
                return;

            var addresses = await _db.Addresses
                .Where(a => addressIdList.Contains(a.Id))
                .ToListAsync(cancellationToken);

            if (addresses.Count < 2)
                return;

            await EnsureCoordinatesAsync(addresses, cancellationToken);
            await EnsureSnappedCoordinatesAsync(addresses, cancellationToken);
            if (HasMissingCoordinates(addresses))
            {
                _logger.LogInformation(
                    "[route.ensure] skipped addresses={Count} reason=missing-coordinates",
                    addresses.Count);
                return;
            }

            var missingPairs = await GetMissingAllPairwiseAsync(addresses, cancellationToken);

            var required = addresses.Count * (addresses.Count - 1);
            _logger.LogInformation(
                "[route.ensure] addresses={Count} required={Required} missing={Missing}",
                addresses.Count, required, missingPairs.Count);

            if (missingPairs.Count == 0)
                return;

            var points = addresses
                .Select(a => new GeoPoint(
                    a.SnappedLatitude  ?? a.Latitude  ?? 0m,
                    a.SnappedLongitude ?? a.Longitude ?? 0m))
                .ToList();

            try
            {
                var matrix = await _distanceApiClient.GetMatrixAsync(points, cancellationToken);
                await UpsertSequentialRoutesAsync(addresses, matrix, missingPairs, cancellationToken);

                _logger.LogInformation(
                    "[route.ensure] fetch-result fetched={Fetched}",
                    missingPairs.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[route.ensure] fetch-failed addresses={Count} missing={Missing}",
                    addresses.Count, missingPairs.Count);
            }
        }

        public async Task<int> RefreshStaleRoutesAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured())
                return 0;

            const int MaxAddressesPerBatch = 30;

            var cutoffUtc = _timeService.GetCurrentUtcTime().Subtract(maxAge);

            var stalePairs = await _db.TravelRoutes
                .AsNoTracking()
                .Where(r => r.CalculatedAt < cutoffUtc)
                .Select(r => new { r.FromAddressId, r.ToAddressId })
                .ToListAsync(cancellationToken);

            if (stalePairs.Count == 0)
            {
                _logger.LogInformation(
                    "route.refresh-stale.done reason=no-stale-routes cutoff={CutoffUtc:O}",
                    cutoffUtc);
                return 0;
            }

            _logger.LogInformation(
                "route.refresh-stale.started stalePairs={StalePairs} cutoff={CutoffUtc:O}",
                stalePairs.Count,
                cutoffUtc);

            var allAddressIds = stalePairs
                .SelectMany(p => new[] { p.FromAddressId, p.ToAddressId })
                .Distinct()
                .ToList();

            var addressLookup = await _db.Addresses
                .Where(a => allAddressIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, cancellationToken);

            await EnsureCoordinatesAsync(addressLookup.Values, cancellationToken);

            var batches = BuildAddressBatches(
                stalePairs.Select(p => (p.FromAddressId, p.ToAddressId)).ToList(),
                MaxAddressesPerBatch);

            _logger.LogInformation(
                "route.refresh-stale.batched batches={Batches} maxAddressesPerBatch={MaxAddressesPerBatch}",
                batches.Count,
                MaxAddressesPerBatch);

            var refreshedTotal = 0;

            foreach (var batch in batches)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var batchAddresses = batch
                    .SelectMany(p => new[] { p.FromAddressId, p.ToAddressId })
                    .Distinct()
                    .Where(addressLookup.ContainsKey)
                    .Select(id => addressLookup[id])
                    .ToList();

                if (batchAddresses.Count < 2)
                {
                    _logger.LogInformation(
                        "route.refresh-stale.batch-skipped reason=insufficient-addresses pairs={Pairs}",
                        batch.Count);
                    continue;
                }

                if (HasMissingCoordinates(batchAddresses))
                {
                    _logger.LogInformation(
                        "route.refresh-stale.batch-skipped reason=missing-coordinates pairs={Pairs}",
                        batch.Count);
                    continue;
                }

                var addressIndex = batchAddresses
                    .Select((a, i) => new { a.Id, Index = i })
                    .ToDictionary(x => x.Id, x => x.Index);

                var pairsToRefresh = batch
                    .Where(p => addressIndex.ContainsKey(p.FromAddressId) && addressIndex.ContainsKey(p.ToAddressId))
                    .Select(p => (
                        p.FromAddressId,
                        p.ToAddressId,
                        MatrixFrom: addressIndex[p.FromAddressId],
                        MatrixTo: addressIndex[p.ToAddressId]))
                    .ToList();

                if (pairsToRefresh.Count == 0)
                    continue;

                try
                {
                    await EnsureSnappedCoordinatesAsync(batchAddresses, cancellationToken);

                    var points = batchAddresses
                        .Select(a => new GeoPoint(
                            a.SnappedLatitude  ?? a.Latitude  ?? 0m,
                            a.SnappedLongitude ?? a.Longitude ?? 0m))
                        .ToList();

                    var matrix = await _distanceApiClient.GetMatrixAsync(points, cancellationToken);
                    await UpsertSequentialRoutesAsync(batchAddresses, matrix, pairsToRefresh, cancellationToken);

                    refreshedTotal += pairsToRefresh.Count;

                    _logger.LogInformation(
                        "route.refresh-stale.batch.completed addresses={Addresses} pairs={Pairs} refreshedTotal={RefreshedTotal}",
                        batchAddresses.Count,
                        pairsToRefresh.Count,
                        refreshedTotal);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "route.refresh-stale.batch.failed addresses={Addresses} pairs={Pairs} refreshedTotal={RefreshedTotal}",
                        batchAddresses.Count,
                        batch.Count,
                        refreshedTotal);
                    break;
                }
            }

            _logger.LogInformation(
                "route.refresh-stale.done refreshed={Refreshed} cutoff={CutoffUtc:O}",
                refreshedTotal,
                cutoffUtc);

            return refreshedTotal;
        }

        private static List<List<(long FromAddressId, long ToAddressId)>> BuildAddressBatches(
            List<(long FromAddressId, long ToAddressId)> pairs,
            int maxAddressesPerBatch)
        {
            var batches = new List<List<(long FromAddressId, long ToAddressId)>>();
            var currentBatch = new List<(long FromAddressId, long ToAddressId)>();
            var currentAddresses = new HashSet<long>();

            foreach (var pair in pairs)
            {
                var willAdd = (!currentAddresses.Contains(pair.FromAddressId) ? 1 : 0)
                            + (!currentAddresses.Contains(pair.ToAddressId) ? 1 : 0);

                if (currentBatch.Count > 0 && currentAddresses.Count + willAdd > maxAddressesPerBatch)
                {
                    batches.Add(currentBatch);
                    currentBatch = [];
                    currentAddresses = [];
                }

                currentBatch.Add(pair);
                currentAddresses.Add(pair.FromAddressId);
                currentAddresses.Add(pair.ToAddressId);
            }

            if (currentBatch.Count > 0)
                batches.Add(currentBatch);

            return batches;
        }

        private async Task<List<(long FromAddressId, long ToAddressId, int MatrixFrom, int MatrixTo)>> GetMissingAllPairwiseAsync(
            IReadOnlyList<AddressEntity> addresses,
            CancellationToken cancellationToken)
        {
            var requiredPairs = Enumerable.Range(0, addresses.Count)
                .SelectMany(i => Enumerable.Range(0, addresses.Count)
                    .Where(j => j != i && addresses[i].Id != addresses[j].Id)
                    .Select(j => (
                        FromAddressId: addresses[i].Id,
                        ToAddressId: addresses[j].Id,
                        MatrixFrom: i,
                        MatrixTo: j)))
                .Distinct()
                .ToList();

            if (requiredPairs.Count == 0)
                return new List<(long FromAddressId, long ToAddressId, int MatrixFrom, int MatrixTo)>();

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

        private async Task<List<(long FromAddressId, long ToAddressId, int MatrixFrom, int MatrixTo)>> GetMissingSequentialPairsAsync(
            IReadOnlyList<AddressEntity> routeAddresses,
            CancellationToken cancellationToken)
        {
            var requiredPairs = Enumerable.Range(0, routeAddresses.Count - 1)
                .SelectMany(i =>
                {
                    var forward = (FromAddressId: routeAddresses[i].Id, ToAddressId: routeAddresses[i + 1].Id, MatrixFrom: i, MatrixTo: i + 1);
                    var reverse = (FromAddressId: routeAddresses[i + 1].Id, ToAddressId: routeAddresses[i].Id, MatrixFrom: i + 1, MatrixTo: i);
                    return new[] { forward, reverse };
                })
                .Where(p => p.FromAddressId != p.ToAddressId)
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
