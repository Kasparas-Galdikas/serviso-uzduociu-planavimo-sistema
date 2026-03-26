using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Backlog;
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

        public BacklogController(IBacklogService backlogService, IUserContextResolver userContextResolver, IScheduleTimeService timeService)
        {
            _backlogService = backlogService;
            _userContextResolver = userContextResolver;
            _timeService = timeService;
        }

        [HttpGet]
        public async Task<IActionResult> GetItems(CancellationToken cancellationToken)
        {
            var userId = _userContextResolver.GetCurrentUser().UserId;
            if (userId == null) return Unauthorized();

            var tzOffset = _timeService.GetBrowserTimezoneOffset(Request);
            var items = await _backlogService.GetItemsAsync(userId.Value, cancellationToken);
            return Ok(items.Select(i => MapItem(i, tzOffset)));
        }

        [HttpPost]
        public async Task<IActionResult> CreateItem([FromBody] SaveBacklogItemRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            var userId = _userContextResolver.GetCurrentUser().UserId;
            if (userId == null) return Unauthorized();

            var tzOffset = _timeService.GetBrowserTimezoneOffset(Request);
            var (item, summary) = await _backlogService.CreateItemAsync(userId.Value, dto, cancellationToken);
            return Ok(new
            {
                item = MapItem(item, tzOffset),
                message = summary,
            });
        }

        [HttpPut("{id:long}")]
        public async Task<IActionResult> UpdateItem(long id, [FromBody] SaveBacklogItemRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            var userId = _userContextResolver.GetCurrentUser().UserId;
            if (userId == null) return Unauthorized();

            var tzOffset = _timeService.GetBrowserTimezoneOffset(Request);
            var (item, summary) = await _backlogService.UpdateItemAsync(userId.Value, id, dto, cancellationToken);
            if (item == null) return NotFound();

            return Ok(new
            {
                item = MapItem(item, tzOffset),
                message = summary,
            });
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
