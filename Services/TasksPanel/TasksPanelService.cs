using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.PlanManagement;

namespace ServiceTaskPlanningSystem.Services.TasksPanel
{
    public class TasksPanelService : ITasksPanelService
    {
        private readonly ScheduleDbContext _db;
        private readonly IPlanService _planService;
        private readonly ILogger<TasksPanelService> _logger;

        public TasksPanelService(ScheduleDbContext db, IPlanService planService, ILogger<TasksPanelService> logger)
        {
            _db = db;
            _planService = planService;
            _logger = logger;
        }

        public async Task<TasksPanelPagedResult> GetItemsAsync(string tab, int page, int pageSize, int? priority = null, CancellationToken ct = default)
        {
            _logger.LogInformation("svc.tasks-panel.list tab={Tab} page={Page} pageSize={PageSize} priority={Priority}", tab, page, pageSize, priority);

            IQueryable<TaskEntity> query = _db.Tasks
                .AsNoTracking()
                .Include(t => t.Plan)
                .Include(t => t.Technician)
                    .ThenInclude(tech => tech.User)
                .Include(t => t.ServiceObject);

            if (tab == "past")
            {
                query = query.Where(t =>
                    (t.Plan != null && t.Plan.Status == PlanStatus.Superseded) ||
                    t.Status == ScheduleTaskStatus.Completed ||
                    t.Status == ScheduleTaskStatus.Canceled);
            }
            else
            {
                query = query.Where(t =>
                    t.Plan != null && t.Plan.Status == PlanStatus.Active &&
                    t.Status == ScheduleTaskStatus.InProgress);
            }

            if (priority.HasValue)
                query = query.Where(t => (int)t.Priority == priority.Value);

            var totalCount = await query.CountAsync(ct);
            var items = await query.OrderByDescending(t => t.StartAt)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

            var loadedIds = items.Select(t => t.Id).ToList();
            var versions = await _db.Tasks
                .AsNoTracking()
                .Where(t => loadedIds.Contains(t.Id))
                .Select(t => new { t.Id, Version = EF.Property<uint>(t, "xmin") })
                .ToDictionaryAsync(x => x.Id, x => x.Version, ct);

            return new TasksPanelPagedResult(items, versions, totalCount, page, pageSize);
        }

        public Task<(int Deleted, int Skipped)> BulkDeleteTasksAsync(long[] ids, UserContext user, CancellationToken ct = default)
            => _planService.BulkDeleteTasksAsync(ids, user, ct);
    }
}
