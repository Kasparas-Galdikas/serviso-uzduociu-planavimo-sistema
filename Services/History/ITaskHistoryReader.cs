using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;

namespace ServiceTaskPlanningSystem.Services.History
{
    public interface ITaskHistoryReader
    {
        /// <summary>
        /// Returns action log entries for a plan, optionally merged with recent backlog
        /// entries for the given user, sorted newest-first and capped at <paramref name="limit"/>.
        /// </summary>
        Task<List<TaskHistoryItemDto>> GetPlanHistoryAsync(
            long planId,
            int limit,
            bool includeBacklog,
            long? userId,
            CancellationToken cancellationToken = default);
    }
}
