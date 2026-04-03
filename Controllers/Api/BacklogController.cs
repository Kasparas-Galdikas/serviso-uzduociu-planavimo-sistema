using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Backlog;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/backlog")]
    [Authorize(Policy = "RequireManager")]
    public class BacklogController : ControllerBase
    {
        private readonly IBacklogService _backlogService;
        private readonly IUserContextResolver _userContextResolver;
        private readonly IScheduleTimeService _timeService;
        private readonly ILogger<BacklogController> _logger;

        public BacklogController(
            IBacklogService backlogService,
            IUserContextResolver userContextResolver,
            IScheduleTimeService timeService,
            ILogger<BacklogController> logger)
        {
            _backlogService = backlogService;
            _userContextResolver = userContextResolver;
            _timeService = timeService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetItems(CancellationToken cancellationToken)
        {
            var userId = _userContextResolver.GetCurrentUser().UserId;
            if (userId == null) return Unauthorized();

            var tzOffset = _timeService.GetBrowserTimezoneOffset(Request);
            var items = await _backlogService.GetItemsAsync(userId.Value, cancellationToken);
            var localNow = _timeService.ConvertUtcToLocal(_timeService.GetCurrentUtcTime(), tzOffset);
            var snappedMinutes = _timeService.GetEarliestCreatableStartMinutes(
                localNow.Date, tzOffset, ScheduleGrid.SlotSizeMinutes, ScheduleGrid.DayStartMinutes);
            var localNowSnapped = localNow.Date.AddMinutes(snappedMinutes);
            return Ok(new
            {
                items = items.Select(i => MapItem(i, tzOffset)),
                localNow = localNowSnapped.ToString("yyyy-MM-ddTHH:mm"),
            });
        }

        [HttpPost]
        public async Task<IActionResult> CreateItem([FromBody] SaveBacklogItemRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            var userId = _userContextResolver.GetCurrentUser().UserId;
            if (userId == null) return Unauthorized();

            try
            {
                var tzOffset = _timeService.GetBrowserTimezoneOffset(Request);
                var (item, summary) = await _backlogService.CreateItemAsync(userId.Value, dto, cancellationToken);
                return Ok(new
                {
                    item = MapItem(item, tzOffset),
                    message = summary,
                });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                _logger.LogError(ex, "api.backlog.create.schema-mismatch");
                return StatusCode(500, new { message = "Nepavyko išsaugoti. Perleiskite programą, kad būtų pritaikyta naujausia migracija." });
            }
        }

        [HttpPut("{id:long}")]
        public async Task<IActionResult> UpdateItem(long id, [FromBody] SaveBacklogItemRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            var userId = _userContextResolver.GetCurrentUser().UserId;
            if (userId == null) return Unauthorized();

            try
            {
                var tzOffset = _timeService.GetBrowserTimezoneOffset(Request);
                var (item, summary) = await _backlogService.UpdateItemAsync(userId.Value, id, dto, cancellationToken);
                if (item == null) return NotFound();

                return Ok(new
                {
                    item = MapItem(item, tzOffset),
                    message = summary,
                });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                _logger.LogError(ex, "api.backlog.update.schema-mismatch");
                return StatusCode(500, new { message = "Nepavyko išsaugoti. Perleiskite programą, kad būtų pritaikyta naujausia migracija." });
            }
        }

        [HttpDelete("{id:long}")]
        public async Task<IActionResult> DeleteItem(long id, CancellationToken cancellationToken)
        {
            var userId = _userContextResolver.GetCurrentUser().UserId;
            if (userId == null) return Unauthorized();

            var (deleted, summary) = await _backlogService.DeleteItemAsync(userId.Value, id, cancellationToken);
            if (!deleted) return NotFound();

            return Ok(new { message = summary });
        }

        [HttpPost("bulk-delete")]
        public async Task<IActionResult> BulkDeleteItems([FromBody] long[] ids, CancellationToken cancellationToken)
        {
            if (ids == null || ids.Length == 0) return BadRequest(new { message = "No ids provided." });

            var userId = _userContextResolver.GetCurrentUser().UserId;
            if (userId == null) return Unauthorized();

            var deleted = await _backlogService.BulkDeleteItemsAsync(userId.Value, ids, cancellationToken);
            return Ok(new { deleted });
        }

        private object MapItem(Data.BacklogItemEntity item, int tzOffset) => new
        {
            id = item.Id,
            title = item.Title,
            priority = (int)item.Priority,
            itemType = (int)item.ItemType,
            skillLevel = item.SkillLevel,
            objectName = item.ObjectName,
            objectAddress = item.ObjectAddress,
            instructions = item.Instructions,
            dueDate = item.DueDate.HasValue
                ? _timeService.ConvertUtcToLocal(item.DueDate.Value, tzOffset).ToString("yyyy-MM-dd")
                : (string?)null,
            createdAt = item.CreatedAt,
        };
    }
}
