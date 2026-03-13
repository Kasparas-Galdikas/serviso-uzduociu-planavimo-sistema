using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.Travel
{
    /// <summary>
    /// Single source of truth for resolving travel time between addresses.
    /// 
    /// All route logic is centralized here: same-address handling, missing route handling,
    /// and duplicate route logic (takes max duration if multiple routes exist).
    /// </summary>
    public interface ITravelTimeResolver
    {
        /// <summary>
        /// Get travel time for a single address pair.
        /// 
        /// Usage: Real-time validation (e.g., checking if a task placement is feasible)
        /// Returns: Travel minutes (0 if same address), null if route not found
        /// </summary>
        Task<int?> GetTravelMinutesAsync(
            long fromAddressId,
            long toAddressId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Get travel times for all address pairs within a set.
        /// 
        /// Usage: Batch operations (repair planning, workload preview)
        /// Returns: Dictionary mapping (fromId, toId) → travelMinutes
        /// 
        /// Note: Missing routes are excluded from result (not included in dict)
        /// </summary>
        Task<Dictionary<(long From, long To), int>> GetTravelMinutesAsync(
            IEnumerable<long> addressIds,
            CancellationToken cancellationToken = default);
    }
}
