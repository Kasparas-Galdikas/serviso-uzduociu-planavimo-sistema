using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.Authorization;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/[controller]")]
    public class PlansController : ControllerBase
    {
        private readonly IPlanService _planService;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly ILogger<PlansController> _logger;
        private readonly ScheduleDbContext _db;
        private readonly IUserContextResolver _userContextResolver;

        public PlansController(IPlanService planService, IEvaluationEngine evaluationEngine, ILogger<PlansController> logger, ScheduleDbContext db, IUserContextResolver userContextResolver)
        {
            _planService = planService;
            _evaluationEngine = evaluationEngine;
            _logger = logger;
            _db = db;
            _userContextResolver = userContextResolver;
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
        [Authorize(Policy = "RequireManager")]
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
        [Authorize(Policy = "RequireManager")]
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

            var planLogs = await _db.TaskActionLogs
                .AsNoTracking()
                .Where(l => l.PlanId == planId)
                .Select(l => new TaskHistoryItemDto
                {
                    Id = l.Id,
                    TaskId = l.TaskId,
                    ActionType = l.ActionType,
                    Outcome = l.Outcome,
                    Summary = l.Summary,
                    OccurredAt = l.OccurredAtUtc,
                    ActorDisplayName = l.ActorName,
                    ActorRole = l.ActorRole,
                    Source = l.Source,
                    DetailsJson = l.DetailsJson
                })
                .ToListAsync(cancellationToken);

            if (includeBacklog)
            {
                var userId = _userContextResolver.GetCurrentUser().UserId;
                if (userId != null)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-7);
                    var backlogLogs = await _db.TaskActionLogs
                        .AsNoTracking()
                        .Where(l => l.PlanId == null
                            && l.ActorUserId == userId
                            && l.OccurredAtUtc >= cutoff)
                        .Select(l => new TaskHistoryItemDto
                        {
                            Id = l.Id,
                            TaskId = l.TaskId,
                            ActionType = l.ActionType,
                            Outcome = l.Outcome,
                            Summary = l.Summary,
                            OccurredAt = l.OccurredAtUtc,
                            ActorDisplayName = l.ActorName,
                            ActorRole = l.ActorRole,
                            Source = l.Source,
                            DetailsJson = l.DetailsJson
                        })
                        .ToListAsync(cancellationToken);

                    planLogs = planLogs
                        .Concat(backlogLogs)
                        .OrderByDescending(l => l.OccurredAt)
                        .Take(clampedLimit)
                        .ToList();
                }
                else
                {
                    planLogs = planLogs
                        .OrderByDescending(l => l.OccurredAt)
                        .Take(clampedLimit)
                        .ToList();
                }
            }
            else
            {
                planLogs = planLogs
                    .OrderByDescending(l => l.OccurredAt)
                    .Take(clampedLimit)
                    .ToList();
            }

            return Ok(planLogs);
        }

        [HttpPost("recalculate-evaluations")]
        [Authorize(Policy = "RequireManager")]
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
