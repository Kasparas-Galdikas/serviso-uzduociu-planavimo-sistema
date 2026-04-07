using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.Common;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;

namespace ServiceTaskPlanningSystem.Services.Backlog
{
    public interface IBacklogService
    {
        Task<PagedResult<BacklogItemEntity>> GetItemsAsync(long userId, int itemType, int page, int pageSize, int? priority = null, CancellationToken ct = default);
        Task<(BacklogItemEntity item, string summary)> CreateItemAsync(long userId, SaveBacklogItemRequestDto dto, CancellationToken ct = default);
        Task<(BacklogItemEntity? item, string summary)> UpdateItemAsync(long userId, long id, SaveBacklogItemRequestDto dto, CancellationToken ct = default);
        Task<(bool deleted, string summary)> DeleteItemAsync(long userId, long id, CancellationToken ct = default);
        Task<int> BulkDeleteItemsAsync(long userId, long[] ids, CancellationToken ct = default);
    }
}
