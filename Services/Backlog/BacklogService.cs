using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Services.History;

namespace ServiceTaskPlanningSystem.Services.Backlog
{
    public class BacklogService : IBacklogService
    {
        private readonly ScheduleDbContext _db;
        private readonly ITaskHistoryWriter _historyWriter;
        private readonly ILogger<BacklogService> _logger;

        public BacklogService(ScheduleDbContext db, ITaskHistoryWriter historyWriter, ILogger<BacklogService> logger)
        {
            _db = db;
            _historyWriter = historyWriter;
            _logger = logger;
        }

        public Task<List<BacklogItemEntity>> GetItemsAsync(long userId, CancellationToken ct = default)
        {
            _logger.LogInformation("svc.backlog.list userId={UserId}", userId);
            return _db.BacklogItems
                .AsNoTracking()
                .Where(b => b.CreatedByUserId == userId)
                .OrderByDescending(b => b.Priority)
                .ThenBy(b => b.DueDate == null ? 1 : 0)
                .ThenBy(b => b.DueDate)
                .ThenByDescending(b => b.CreatedAt)
                .ToListAsync(ct);
        }

        public async Task<(BacklogItemEntity item, string summary)> CreateItemAsync(long userId, SaveBacklogItemRequestDto dto, CancellationToken ct = default)
        {
            var item = new BacklogItemEntity
            {
                CreatedByUserId = userId,
                Title = dto.Title.Trim(),
                Priority = dto.Priority ?? TaskPriority.Low,
                ItemType = dto.ItemType,
                SkillLevel = dto.SkillLevel?.Trim() ?? string.Empty,
                ObjectName = dto.ObjectName?.Trim() ?? string.Empty,
                ObjectAddress = dto.ObjectAddress?.Trim() ?? string.Empty,
                Instructions = dto.Instructions?.Trim() ?? string.Empty,
                DueDate = dto.DueDate,
                CreatedAt = DateTime.UtcNow,
            };

            _db.BacklogItems.Add(item);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("svc.backlog.create id={Id} userId={UserId} title={Title}", item.Id, userId, item.Title);

            var (summary, outcome) = TaskHistoryMessages.ForBacklog("backlog_create", item.Title);
            await _historyWriter.RecordAsync(null, null, "backlog_create", outcome, summary, userId, "manager", cancellationToken: ct);

            return (item, summary);
        }

        public async Task<(BacklogItemEntity? item, string summary)> UpdateItemAsync(long userId, long id, SaveBacklogItemRequestDto dto, CancellationToken ct = default)
        {
            var item = await _db.BacklogItems
                .FirstOrDefaultAsync(b => b.Id == id && b.CreatedByUserId == userId, ct);

            if (item == null)
            {
                _logger.LogWarning("svc.backlog.update.notfound id={Id} userId={UserId}", id, userId);
                return (null, string.Empty);
            }

            item.Title = dto.Title.Trim();
            item.Priority = dto.Priority ?? item.Priority;
            item.ItemType = dto.ItemType;
            item.SkillLevel = dto.SkillLevel?.Trim() ?? string.Empty;
            item.ObjectName = dto.ObjectName?.Trim() ?? string.Empty;
            item.ObjectAddress = dto.ObjectAddress?.Trim() ?? string.Empty;
            item.Instructions = dto.Instructions?.Trim() ?? string.Empty;
            item.DueDate = dto.DueDate;

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("svc.backlog.update id={Id} userId={UserId} title={Title}", item.Id, userId, item.Title);

            var (summary, outcome) = TaskHistoryMessages.ForBacklog("backlog_update", item.Title);
            await _historyWriter.RecordAsync(null, null, "backlog_update", outcome, summary, userId, "manager", cancellationToken: ct);

            return (item, summary);
        }

        public async Task<(bool deleted, string summary)> DeleteItemAsync(long userId, long id, CancellationToken ct = default)
        {
            var item = await _db.BacklogItems
                .FirstOrDefaultAsync(b => b.Id == id && b.CreatedByUserId == userId, ct);

            if (item == null)
            {
                _logger.LogWarning("svc.backlog.delete.notfound id={Id} userId={UserId}", id, userId);
                return (false, string.Empty);
            }

            var title = item.Title;
            _db.BacklogItems.Remove(item);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("svc.backlog.delete id={Id} userId={UserId} title={Title}", id, userId, title);

            var (summary, outcome) = TaskHistoryMessages.ForBacklog("backlog_delete", title);
            await _historyWriter.RecordAsync(null, null, "backlog_delete", outcome, summary, userId, "manager", cancellationToken: ct);

            return (true, summary);
        }

        public async Task<int> BulkDeleteItemsAsync(long userId, long[] ids, CancellationToken ct = default)
        {
            var items = await _db.BacklogItems
                .Where(b => ids.Contains(b.Id) && b.CreatedByUserId == userId)
                .ToListAsync(ct);

            if (items.Count == 0) return 0;

            _db.BacklogItems.RemoveRange(items);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("svc.backlog.bulk-delete count={Count} userId={UserId}", items.Count, userId);

            var (summary, outcome) = TaskHistoryMessages.ForBacklog("backlog_delete", $"{items.Count} įrašai");
            await _historyWriter.RecordAsync(null, null, "backlog_delete", outcome, summary, userId, "manager", cancellationToken: ct);

            return items.Count;
        }
    }
}
