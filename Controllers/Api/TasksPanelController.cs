using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.TasksPanel;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/tasks-panel")]
    [Authorize(Policy = "RequireManager")]
    public class TasksPanelController : ControllerBase
    {
        private readonly ITasksPanelService _tasksPanelService;
        private readonly IScheduleTimeService _timeService;
        private readonly ILogger<TasksPanelController> _logger;

        public TasksPanelController(
            ITasksPanelService tasksPanelService,
            IScheduleTimeService timeService,
            ILogger<TasksPanelController> logger)
        {
            _tasksPanelService = tasksPanelService;
            _timeService = timeService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetItems(
            [FromQuery] string tab = "active",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            [FromQuery] int? priority = null,
            CancellationToken cancellationToken = default)
        {
            if (tab != "active" && tab != "past")
                tab = "active";

            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 100) pageSize = 25;

            var tzOffset = _timeService.GetBrowserTimezoneOffset(Request);
            var result = await _tasksPanelService.GetItemsAsync(tab, page, pageSize, priority, cancellationToken);

            return Ok(new
            {
                items = result.Items.Select(t => new
                {
                    id = t.Id,
                    version = result.Versions.TryGetValue(t.Id, out var v) ? (long)v : 0L,
                    title = t.Title,
                    priority = (int)t.Priority,
                    startAt = _timeService.ConvertUtcToLocal(t.StartAt, tzOffset).ToString("yyyy-MM-ddTHH:mm"),
                    endAt = _timeService.ConvertUtcToLocal(t.EndAt, tzOffset).ToString("yyyy-MM-ddTHH:mm"),
                    planStatus = t.Plan?.Status.ToString().ToLowerInvariant() ?? "unknown",
                    taskStatus = t.Status.ToString().ToLowerInvariant(),
                    technicianName = t.Technician?.User?.Name ?? string.Empty,
                    objectName = t.ServiceObject?.Name ?? string.Empty,
                }),
                totalCount = result.TotalCount,
                page = result.Page,
                pageSize = result.PageSize,
                hasMore = result.HasMore,
                tab,
            });
        }

        [HttpPost("bulk-delete")]
        public async Task<IActionResult> BulkDelete([FromBody] long[] ids, CancellationToken cancellationToken)
        {
            if (ids == null || ids.Length == 0)
                return BadRequest(new { message = "No ids provided." });

            _logger.LogInformation("api.tasks-panel.bulk-delete count={Count}", ids.Length);
            var deleted = await _tasksPanelService.BulkDeleteTasksAsync(ids, cancellationToken);
            return Ok(new { deleted });
        }
    }
}
