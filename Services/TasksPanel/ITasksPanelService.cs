using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.Common;

namespace ServiceTaskPlanningSystem.Services.TasksPanel
{
    public sealed record TasksPanelPagedResult(
        List<TaskEntity> Items,
        Dictionary<long, uint> Versions,
        int TotalCount,
        int Page,
        int PageSize)
    {
        public bool HasMore => Page * PageSize < TotalCount;
    }

    public interface ITasksPanelService
    {
        Task<TasksPanelPagedResult> GetItemsAsync(string tab, int page, int pageSize, int? priority = null, CancellationToken ct = default);
        Task<int> BulkDeleteTasksAsync(long[] ids, CancellationToken ct = default);
    }
}
