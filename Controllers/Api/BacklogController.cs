using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Backlog;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/backlog")]
    [Authorize(Policy = "RequireManager")]
    public class BacklogController : ControllerBase
    {
        private readonly IBacklogService _backlogService;
        private readonly IUserContextResolver _userContextResolver;

        public BacklogController(IBacklogService backlogService, IUserContextResolver userContextResolver)
        {
            _backlogService = backlogService;
            _userContextResolver = userContextResolver;
        }

        [HttpGet]
        public async Task<IActionResult> GetItems(CancellationToken cancellationToken)
        {
            var userId = _userContextResolver.GetCurrentUser().UserId;
            if (userId == null) return Unauthorized();

            var items = await _backlogService.GetItemsAsync(userId.Value, cancellationToken);
            return Ok(items);
        }

        [HttpPost]
        public async Task<IActionResult> CreateItem([FromBody] SaveBacklogItemRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            var userId = _userContextResolver.GetCurrentUser().UserId;
            if (userId == null) return Unauthorized();

            var (item, summary) = await _backlogService.CreateItemAsync(userId.Value, dto, cancellationToken);
            return Ok(new
            {
                item = MapItem(item),
                message = summary,
            });
        }

        [HttpPut("{id:long}")]
        public async Task<IActionResult> UpdateItem(long id, [FromBody] SaveBacklogItemRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            var userId = _userContextResolver.GetCurrentUser().UserId;
            if (userId == null) return Unauthorized();

            var (item, summary) = await _backlogService.UpdateItemAsync(userId.Value, id, dto, cancellationToken);
            if (item == null) return NotFound();

            return Ok(new
            {
                item = MapItem(item),
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

        private static object MapItem(Data.BacklogItemEntity item) => new
        {
            id = item.Id,
            title = item.Title,
            priority = (int)item.Priority,
            skillLevel = item.SkillLevel,
            objectName = item.ObjectName,
            objectAddress = item.ObjectAddress,
            instructions = item.Instructions,
            createdAt = item.CreatedAt,
        };
    }
}
