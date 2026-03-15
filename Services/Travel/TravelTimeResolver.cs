using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.Travel
{
    /// <summary>
    /// Resolves travel time from the TravelRoutes database table.
    /// 
    /// This is the SINGLE SOURCE OF TRUTH for all route logic in the system:
    /// - Same-address handling (0 minutes)
    /// - GroupBy logic for handling duplicate routes (takes max duration)
    /// - Missing route logging and handling
    /// 
    /// All services that need travel time should use this resolver, not query
    /// TravelRoutes directly.
    /// </summary>
    public sealed class TravelTimeResolver : ITravelTimeResolver
    {
        private readonly ScheduleDbContext _db;
        private readonly ILogger<TravelTimeResolver> _logger;

        public TravelTimeResolver(ScheduleDbContext db, ILogger<TravelTimeResolver> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Get travel time for a single address pair.
        /// </summary>
        public async Task<int?> GetTravelMinutesAsync(
            long fromAddressId,
            long toAddressId,
            CancellationToken cancellationToken = default)
        {
            // Same address = no travel needed
            if (fromAddressId == toAddressId)
            {
                return 0;
            }

            var route = await _db.TravelRoutes
                .AsNoTracking()
                .Where(r => r.FromAddressId == fromAddressId && r.ToAddressId == toAddressId)
                .OrderByDescending(x => x.DurationMinutes)
                .FirstOrDefaultAsync(cancellationToken);

            if (route == null)
            {
                _logger.LogWarning(
                    "Travel route missing: FromAddressId={FromAddressId}, ToAddressId={ToAddressId}",
                    fromAddressId, toAddressId);
                return null;
            }

            return route.DurationMinutes;
        }

        /// <summary>
        /// Get travel times for all address pairs within a set.
        /// 
        /// This is the centralized location for GroupBy logic that was previously
        /// duplicated across ScheduleRepairService and PlanService.
        /// </summary>
        public async Task<Dictionary<(long From, long To), int>> GetTravelMinutesAsync(
            IEnumerable<long> addressIds,
            CancellationToken cancellationToken = default)
        {
            var addressIdList = addressIds.Distinct().ToList();

            var routes = await _db.TravelRoutes
                .AsNoTracking()
                .Where(r => addressIdList.Contains(r.FromAddressId) && 
                            addressIdList.Contains(r.ToAddressId))
                .ToListAsync(cancellationToken);

            // Group by (from, to) and take highest duration if duplicates exist
            // (centralizes logic that was previously in ScheduleRepairService.BuildRouteLookupAsync)
            var lookup = routes
                .GroupBy(r => (r.FromAddressId, r.ToAddressId))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(x => x.DurationMinutes).First().DurationMinutes);

            _logger.LogDebug(
                "Built travel lookup for {AddressCount} addresses: found {RouteCount} routes",
                addressIdList.Count, lookup.Count);

            return lookup;
        }
    }
}
