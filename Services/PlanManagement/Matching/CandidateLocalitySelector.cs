using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Matching;

internal enum CandidateLocalityTier
{
    GlobalFallback = 0,
    NearestCity    = 1,
    SameCity       = 2
}

internal sealed record CandidateLocalityPool(
    IReadOnlyList<TechnicianEntity> Technicians,
    CandidateLocalityTier Tier,
    string Label);

internal sealed class CandidateLocalitySelector
{
    // Maximum number of nearest cities to try in Phase 2 before giving up and falling back.
    private const int MaxNearestCitiesToTry = 3;

    // Default max range used in Phase 2 when a city group has no address coordinates.
    private const double DefaultNearestCityRangeKm = 150.0;

    private readonly ILogger<CandidateLocalitySelector> _logger;

    public CandidateLocalitySelector(ILogger<CandidateLocalitySelector> logger)
    {
        _logger = logger;
    }

    public CandidateLocalityPool SelectPool(
        IReadOnlyList<TechnicianEntity> eligibleTechnicians,
        ServiceObjectEntity serviceObject)
    {
        var targetCity = serviceObject.Address?.City?.Trim();
        var taskCoords = GetCoords(serviceObject.Address);

        // ── Phase 1: SameCity ──────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(targetCity))
        {
            var sameCity = eligibleTechnicians
                .Where(t => string.Equals(
                    t.WorkplaceAddress?.City?.Trim(),
                    targetCity,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sameCity.Count > 0)
            {
                var rangeAdmitted = new List<TechnicianEntity>();
                var maxRangeKm = 0.0;

                if (taskCoords.HasValue)
                {
                    var sameCityWithCoords = sameCity
                        .Select(t => (tech: t, coords: GetCoords(t.WorkplaceAddress)))
                        .Where(x => x.coords.HasValue)
                        .ToList();

                    if (sameCityWithCoords.Count > 0)
                    {
                        maxRangeKm = sameCityWithCoords
                            .Max(x => HaversineKm(
                                x.coords!.Value.Lat, x.coords.Value.Lng,
                                taskCoords.Value.Lat, taskCoords.Value.Lng));

                        var sameCityIds = sameCity.Select(t => t.Id).ToHashSet();
                        rangeAdmitted = eligibleTechnicians
                            .Where(t => !sameCityIds.Contains(t.Id))
                            .Where(t =>
                            {
                                var coords = GetCoords(t.WorkplaceAddress);
                                if (!coords.HasValue) return false;
                                var dist = HaversineKm(
                                    coords.Value.Lat, coords.Value.Lng,
                                    taskCoords.Value.Lat, taskCoords.Value.Lng);
                                return dist <= maxRangeKm;
                            })
                            .ToList();

                        foreach (var t in rangeAdmitted)
                        {
                            var coords = GetCoords(t.WorkplaceAddress)!.Value;
                            var dist = HaversineKm(
                                coords.Lat, coords.Lng,
                                taskCoords.Value.Lat, taskCoords.Value.Lng);
                            _logger.LogInformation(
                                "svc.plan.auto-assign.locality.range-admitted technicianId={TechnicianId} technicianName={TechnicianName} city={City} distanceKm={DistanceKm:F1} rangeLimitKm={RangeLimitKm:F1}",
                                t.Id,
                                t.User?.Name ?? $"Technician #{t.Id}",
                                t.WorkplaceAddress?.City,
                                dist,
                                maxRangeKm);
                        }
                    }
                }

                var pool = sameCity.Concat(rangeAdmitted).OrderBy(t => t.Id).ToList();
                _logger.LogInformation(
                    "svc.plan.auto-assign.locality.phase phase=SameCity targetCity={TargetCity} sameCityCount={SameCityCount} rangeAdmittedCount={RangeAdmittedCount} maxRangeKm={MaxRangeKm:F1} totalPoolCount={TotalPoolCount}",
                    targetCity,
                    sameCity.Count,
                    rangeAdmitted.Count,
                    maxRangeKm,
                    pool.Count);
                return new CandidateLocalityPool(pool, CandidateLocalityTier.SameCity, targetCity);
            }

            _logger.LogInformation(
                "svc.plan.auto-assign.locality.phase-fallback reason=no-same-city-technicians targetCity={TargetCity} nextPhase=NearestCity",
                targetCity);
        }
        else
        {
            _logger.LogInformation(
                "svc.plan.auto-assign.locality.phase-fallback reason=no-city-on-object nextPhase=NearestCity");
        }

        // ── Phase 2: NearestCity ───────────────────────────────────────────────────
        if (taskCoords.HasValue)
        {
            var cityGroups = eligibleTechnicians
                .Where(t => !string.IsNullOrWhiteSpace(t.WorkplaceAddress?.City))
                .Where(t => !string.Equals(
                    t.WorkplaceAddress!.City!.Trim(),
                    targetCity,
                    StringComparison.OrdinalIgnoreCase))
                .GroupBy(t => t.WorkplaceAddress!.City!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var techsWithCoords = g
                        .Select(t => (tech: t, coords: GetCoords(t.WorkplaceAddress)))
                        .Where(x => x.coords.HasValue)
                        .ToList();

                    var centroidLat = techsWithCoords.Count > 0
                        ? techsWithCoords.Average(x => x.coords!.Value.Lat)
                        : (double?)null;
                    var centroidLng = techsWithCoords.Count > 0
                        ? techsWithCoords.Average(x => x.coords!.Value.Lng)
                        : (double?)null;

                    var distKm = centroidLat.HasValue && centroidLng.HasValue
                        ? HaversineKm(centroidLat.Value, centroidLng.Value, taskCoords.Value.Lat, taskCoords.Value.Lng)
                        : double.MaxValue;

                    return (City: g.Key, Techs: g.ToList(), DistKm: distKm);
                })
                .Where(x => x.DistKm < double.MaxValue)
                .OrderBy(x => x.DistKm)
                .ToList();

            _logger.LogInformation(
                "svc.plan.auto-assign.locality.city-order cities=[{Cities}]",
                string.Join(", ", cityGroups.Take(MaxNearestCitiesToTry).Select(x => $"{x.City}:{x.DistKm:F0}km")));

            var triedCities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(targetCity))
                triedCities.Add(targetCity);

            foreach (var cityGroup in cityGroups.Take(MaxNearestCitiesToTry))
            {
                triedCities.Add(cityGroup.City);
                var cityTechs = cityGroup.Techs;
                if (cityTechs.Count == 0) continue;

                var cityTechsWithCoords = cityTechs
                    .Select(t => (tech: t, coords: GetCoords(t.WorkplaceAddress)))
                    .Where(x => x.coords.HasValue)
                    .ToList();

                var maxRangeKm = cityTechsWithCoords.Count > 0
                    ? cityTechsWithCoords.Max(x => HaversineKm(
                        x.coords!.Value.Lat, x.coords.Value.Lng,
                        taskCoords.Value.Lat, taskCoords.Value.Lng))
                    : DefaultNearestCityRangeKm;

                var cityTechIds = cityTechs.Select(t => t.Id).ToHashSet();
                var rangeAdmitted = eligibleTechnicians
                    .Where(t => !cityTechIds.Contains(t.Id))
                    .Where(t =>
                    {
                        var city = t.WorkplaceAddress?.City?.Trim();
                        if (city != null && triedCities.Contains(city)) return false;
                        var coords = GetCoords(t.WorkplaceAddress);
                        if (!coords.HasValue) return false;
                        var dist = HaversineKm(
                            coords.Value.Lat, coords.Value.Lng,
                            taskCoords.Value.Lat, taskCoords.Value.Lng);
                        return dist <= maxRangeKm;
                    })
                    .ToList();

                var pool = cityTechs.Concat(rangeAdmitted).OrderBy(t => t.Id).ToList();
                if (pool.Count == 0) continue;

                _logger.LogInformation(
                    "svc.plan.auto-assign.locality.phase phase=NearestCity city={City} distanceKm={DistanceKm:F1} techCount={TechCount} rangeAdmittedCount={RangeAdmittedCount} maxRangeKm={MaxRangeKm:F1} totalPoolCount={TotalPoolCount}",
                    cityGroup.City,
                    cityGroup.DistKm,
                    cityTechs.Count,
                    rangeAdmitted.Count,
                    maxRangeKm,
                    pool.Count);
                return new CandidateLocalityPool(pool, CandidateLocalityTier.NearestCity, cityGroup.City);
            }
        }
        else
        {
            _logger.LogInformation(
                "svc.plan.auto-assign.locality.no-coords objectName={ObjectName} skipping NearestCity phase",
                serviceObject.Name);
        }

        // ── Phase 3: GlobalFallback ────────────────────────────────────────────────
        var globalPool = eligibleTechnicians.OrderBy(t => t.Id).ToList();
        _logger.LogInformation(
            "svc.plan.auto-assign.locality.phase phase=GlobalFallback techCount={TechCount}",
            globalPool.Count);
        return new CandidateLocalityPool(globalPool, CandidateLocalityTier.GlobalFallback, "global");
    }

    private static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLng = (lng2 - lng1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static (double Lat, double Lng)? GetCoords(AddressEntity? address)
    {
        if (address == null) return null;
        if (address.SnappedLatitude.HasValue && address.SnappedLongitude.HasValue)
            return ((double)address.SnappedLatitude.Value, (double)address.SnappedLongitude.Value);
        if (address.Latitude.HasValue && address.Longitude.HasValue)
            return ((double)address.Latitude.Value, (double)address.Longitude.Value);
        return null;
    }
}
