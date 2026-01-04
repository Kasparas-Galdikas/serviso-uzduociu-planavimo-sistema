using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Services.Plans;

namespace ServiceTaskPlanningSystem.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PlansController : ControllerBase
    {
        private readonly IPlanService _planService;

        public PlansController(IPlanService planService)
        {
            _planService = planService;
        }

        [HttpGet("state")]
        public async Task<IActionResult> GetState(CancellationToken cancellationToken)
        {
            var active = await _planService.GetActivePlanAsync(cancellationToken);
            var draft = await _planService.GetDraftPlanAsync(cancellationToken);

            return Ok(new
            {
                hasDraft = draft != null,
                activePlanId = active?.Id,
                draftPlanId = draft?.Id
            });
        }

        [HttpPost("draft")]
        public async Task<IActionResult> CreateDraft(CancellationToken cancellationToken)
        {
            var draft = await _planService.CreateDraftFromActiveAsync(cancellationToken);
            return Ok(new { draftPlanId = draft.Id });
        }

        [HttpPost("publish")]
        public async Task<IActionResult> PublishDraft(CancellationToken cancellationToken)
        {
            await _planService.PublishDraftAsync(cancellationToken);
            return Ok();
        }
    }
}
