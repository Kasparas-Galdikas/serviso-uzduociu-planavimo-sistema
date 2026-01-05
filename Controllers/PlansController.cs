using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Services.Plans;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PlansController : ControllerBase
    {
        private readonly IPlanService _planService;
        private readonly ITaskEvaluationService _taskEvaluationService;

        public PlansController(IPlanService planService, ITaskEvaluationService taskEvaluationService)
        {
            _planService = planService;
            _taskEvaluationService = taskEvaluationService;
        }

        public class CreateDraftRequest
        {
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public int? Weeks { get; set; }
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
        public async Task<IActionResult> CreateDraft([FromBody] CreateDraftRequest? request, CancellationToken cancellationToken)
        {
            try
            {
                var draft = await _planService.CreateDraftFromActiveAsync(request?.StartDate, request?.EndDate, request?.Weeks, cancellationToken);
                return Ok(new { draftPlanId = draft.Id });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("publish")]
        public async Task<IActionResult> PublishDraft(CancellationToken cancellationToken)
        {
            try
            {
                await _planService.PublishDraftAsync(cancellationToken);
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("discard")]
        public async Task<IActionResult> DiscardDraft(CancellationToken cancellationToken)
        {
            await _planService.DiscardDraftAsync(cancellationToken);
            return Ok();
        }

        [HttpPost("recalculate-evaluations")]
        public async Task<IActionResult> RecalculateEvaluations(CancellationToken cancellationToken)
        {
            var active = await _planService.GetActivePlanAsync(cancellationToken);
            var draft = await _planService.GetDraftPlanAsync(cancellationToken);

            if (active == null && draft == null)
            {
                return Ok(new { message = "No active or draft plans to recalculate." });
            }

            if (active != null)
            {
                await _taskEvaluationService.RecalculatePlanAsync(active.Id, cancellationToken);
            }

            if (draft != null)
            {
                await _taskEvaluationService.RecalculatePlanAsync(draft.Id, cancellationToken);
            }

            return Ok(new
            {
                recalculatedActive = active?.Id,
                recalculatedDraft = draft?.Id
            });
        }
    }
}
