using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Services.Plans;
using ServiceTaskPlanningSystem.Data;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.Evaluation;

namespace ServiceTaskPlanningSystem.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PlansController : ControllerBase
    {
        private readonly IPlanService _planService;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly ILogger<PlansController> _logger;

        public PlansController(IPlanService planService, IEvaluationEngine evaluationEngine, ILogger<PlansController> logger)
        {
            _planService = planService;
            _evaluationEngine = evaluationEngine;
            _logger = logger;
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
            _logger.LogInformation("api.plan.state active={Active} draft={Draft}", active?.Id, draft?.Id);

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
                _logger.LogInformation("api.plan.draft.created planId={PlanId}", draft.Id);
                Response.Headers["X-Trace-Summary"] = $"draft.created id={draft.Id} start={draft.PeriodStart:yyyy-MM-dd} end={draft.PeriodEnd:yyyy-MM-dd} weeks={draft.Weeks}";
                return Ok(new { draftPlanId = draft.Id });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "api.plan.draft.failed");
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("publish")]
        public async Task<IActionResult> PublishDraft(CancellationToken cancellationToken)
        {
            try
            {
                await _planService.PublishDraftAsync(cancellationToken);
                _logger.LogInformation("api.plan.publish.ok");
                Response.Headers["X-Trace-Summary"] = "plan.publish.ok";
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "api.plan.publish.failed");
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("discard")]
        public async Task<IActionResult> DiscardDraft(CancellationToken cancellationToken)
        {
            await _planService.DiscardDraftAsync(cancellationToken);
            _logger.LogInformation("api.plan.discard.ok");
            Response.Headers["X-Trace-Summary"] = "plan.discard.ok";
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
                await _evaluationEngine.RecalculatePlanAsync(active.Id, cancellationToken);
            }

            if (draft != null)
            {
                await _evaluationEngine.RecalculatePlanAsync(draft.Id, cancellationToken);
            }

            return Ok(new
            {
                recalculatedActive = active?.Id,
                recalculatedDraft = draft?.Id
            });
        }
    }
}
