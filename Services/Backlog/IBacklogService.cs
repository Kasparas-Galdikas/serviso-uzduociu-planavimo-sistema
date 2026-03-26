using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;

namespace ServiceTaskPlanningSystem.Services.Backlog
{
    public interface IBacklogService
    {
        Task<List<BacklogItemEntity>> GetItemsAsync(long userId, CancellationToken ct = default);
        Task<(BacklogItemEntity item, string summary)> CreateItemAsync(long userId, SaveBacklogItemRequestDto dto, CancellationToken ct = default);
        Task<(BacklogItemEntity? item, string summary)> UpdateItemAsync(long userId, long id, SaveBacklogItemRequestDto dto, CancellationToken ct = default);
        Task<(bool deleted, string summary)> DeleteItemAsync(long userId, long id, CancellationToken ct = default);
        Task<int> BulkDeleteItemsAsync(long userId, long[] ids, CancellationToken ct = default);
    }
}
