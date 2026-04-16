using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.History;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.Authorization;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/[controller]")]
    public class PlansController : ControllerBase
    {
        private readonly IPlanService _planService;
        private readonly ILogger<PlansController> _logger;
        private readonly ITaskHistoryReader _historyReader;
        private readonly IUserContextResolver _userContextResolver;

        public PlansController(IPlanService planService, ILogger<PlansController> logger, ITaskHistoryReader historyReader, IUserContextResolver userContextResolver)
        {
            _planService = planService;
            _logger = logger;
            _historyReader = historyReader;
            _userContextResolver = userContextResolver;
        }

        public class EnsureBacklogDraftRequest
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


        [HttpPost("discard")]
        [Authorize(Policy = "RequireManager")]
        public async Task<IActionResult> DiscardDraft(CancellationToken cancellationToken)
        {
            await _planService.DiscardDraftAsync(cancellationToken);
            _logger.LogInformation("api.plan.discard.ok");
            Response.Headers["X-Trace-Summary"] = "plan.discard.ok";
            return Ok();
        }

        [HttpGet("{planId:long}/history")]
        [Authorize(Policy = "RequireManager")]
        public async Task<IActionResult> GetPlanHistory(long planId, [FromQuery] int limit = 50, [FromQuery] bool includeBacklog = false, CancellationToken cancellationToken = default)
        {
            var clampedLimit = Math.Clamp(limit, 1, 200);
            var userId = _userContextResolver.GetCurrentUser().UserId;
            var logs = await _historyReader.GetPlanHistoryAsync(planId, clampedLimit, includeBacklog, userId, cancellationToken);
            return Ok(logs);
        }

        [HttpPost("recalculate-evaluations")]
        [Authorize(Policy = "RequireManager")]
        public async Task<IActionResult> RecalculateEvaluations(CancellationToken cancellationToken)
        {
            var (activePlanId, draftPlanId) = await _planService.RecalculateCurrentPlansAsync(cancellationToken);

            if (activePlanId == null && draftPlanId == null)
            {
                return Ok(new { message = "No active or draft plans to recalculate." });
            }

            return Ok(new
            {
                recalculatedActive = activePlanId,
                recalculatedDraft = draftPlanId
            });
        }

        [HttpPost("backlog-draft/ensure")]
        [Authorize(Policy = "RequireManager")]
        public async Task<IActionResult> EnsureBacklogDraft([FromBody] EnsureBacklogDraftRequest? request, CancellationToken cancellationToken)
        {
            try
            {
                var hadDraftBefore = await _planService.GetDraftPlanAsync(cancellationToken) != null;
                var draft = await _planService.EnsureBacklogDraftAsync(request?.StartDate, request?.EndDate, request?.Weeks, cancellationToken);
                _logger.LogInformation(
                    "api.plan.backlog-draft.ensure.ok planId={PlanId} refreshed={Refreshed} requestedStart={RequestedStart} requestedEnd={RequestedEnd} periodStart={PeriodStart} periodEnd={PeriodEnd}",
                    draft.Id,
                    hadDraftBefore,
                    request?.StartDate,
                    request?.EndDate,
                    draft.PeriodStart,
                    draft.PeriodEnd);
                return Ok(new
                {
                    draftPlanId = draft.Id,
                    created = !hadDraftBefore,
                    refreshed = hadDraftBefore,
                    periodStart = draft.PeriodStart.ToString("yyyy-MM-dd"),
                    periodEnd = draft.PeriodEnd.ToString("yyyy-MM-dd")
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "api.plan.backlog-draft.ensure.failed");
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("backlog-draft/apply-to-active")]
        [HttpPost("backlog-draft/publish")]
        [Authorize(Policy = "RequireManager")]
        public async Task<IActionResult> ApplyBacklogDraftToActive(CancellationToken cancellationToken)
        {
            try
            {
                await _planService.ApplyDraftToActiveAsync(cancellationToken);
                _logger.LogInformation("api.plan.backlog-draft.apply.ok");
                Response.Headers["X-Trace-Summary"] = "plan.backlog-draft.apply.ok";
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "api.plan.backlog-draft.apply.failed");
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
