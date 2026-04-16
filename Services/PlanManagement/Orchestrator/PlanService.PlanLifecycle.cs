using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator
{
    internal class PlanServicePlanLifecycle
    {
        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly IPlanningCalendar _planningCalendar;
        private readonly IScheduleTimeService _timeService;
        private readonly ILogger<PlanServicePlanLifecycle> _logger;

        public PlanServicePlanLifecycle(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            IPlanningCalendar planningCalendar,
            IScheduleTimeService timeService,
            ILogger<PlanServicePlanLifecycle> logger)
        {
            _db = db;
            _evaluationEngine = evaluationEngine;
            _planningCalendar = planningCalendar;
            _timeService = timeService;
            _logger = logger;
        }

        public Task<PlanEntity?> GetActivePlanAsync(CancellationToken cancellationToken = default) =>
            _db.Plans
               .AsNoTracking()
               .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);

        public Task<PlanEntity?> GetDraftPlanAsync(CancellationToken cancellationToken = default) =>
            _db.Plans
               .AsNoTracking()
               .FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);

        public async Task<PlanEntity> EnsureBacklogDraftAsync(
            DateTime? requestedStart = null,
            DateTime? requestedEnd = null,
            int? requestedWeeks = null,
            CancellationToken cancellationToken = default)
        {
            var activePlan = await _db.Plans
                .Include(p => p.Tasks)
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);

            if (activePlan == null)
            {
                throw new InvalidOperationException("No active plan found to refresh draft from.");
            }

            var fallbackWeeks = activePlan.Weeks > 0 ? activePlan.Weeks : 4;
            var window = BuildBacklogDraftWindow(requestedStart, requestedEnd, requestedWeeks, fallbackWeeks);
            var existingDraft = await _db.Plans
                .Include(p => p.Tasks)
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);

            _logger.LogInformation(
                "svc.plan.backlog-draft.ensure.start requestedStart={RequestedStart} requestedEnd={RequestedEnd} requestedWeeks={RequestedWeeks} activePlanId={ActivePlanId} activeTasks={ActiveTaskCount} existingDraftId={ExistingDraftId} existingDraftTasks={ExistingDraftTaskCount}",
                requestedStart,
                requestedEnd,
                requestedWeeks,
                activePlan.Id,
                activePlan.Tasks.Count,
                existingDraft?.Id,
                existingDraft?.Tasks.Count ?? 0);

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            PlanEntity draftPlan;
            if (existingDraft == null)
            {
                draftPlan = new PlanEntity
                {
                    PeriodStart = window.Start,
                    PeriodEnd = window.End,
                    Weeks = window.Weeks,
                    Status = PlanStatus.Draft,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = activePlan.CreatedBy
                };

                _db.Plans.Add(draftPlan);
                await _db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                draftPlan = existingDraft;
            }

            var requestedRangeStart = window.Start.Date;
            var requestedRangeEnd = window.End.Date;
            var ensuredStart = draftPlan.Tasks.Any()
                ? new[] { requestedRangeStart, draftPlan.PeriodStart.Date, draftPlan.Tasks.Min(t => t.StartAt.Date) }.Min()
                : new[] { requestedRangeStart, draftPlan.PeriodStart.Date }.Min();
            var ensuredEnd = draftPlan.Tasks.Any()
                ? new[] { requestedRangeEnd, draftPlan.PeriodEnd.Date, draftPlan.Tasks.Max(t => t.EndAt.Date) }.Max()
                : new[] { requestedRangeEnd, draftPlan.PeriodEnd.Date }.Max();

            var existingSourceTaskIds = draftPlan.Tasks
                .Where(t => t.SourceTaskId.HasValue)
                .Select(t => t.SourceTaskId!.Value)
                .ToHashSet();

            var generatedTasks = await GenerateTasksFromActivePlanAsync(activePlan, ensuredStart, ensuredEnd, draftPlan.Id, cancellationToken);
            var seededTasks = generatedTasks
                .Where(t => !t.SourceTaskId.HasValue || !existingSourceTaskIds.Contains(t.SourceTaskId.Value))
                .ToList();

            _logger.LogInformation(
                "svc.plan.backlog-draft.ensure.seed-check draftPlanId={DraftPlanId} requestedRange={RequestedStart}-{RequestedEnd} ensuredRange={EnsuredStart}-{EnsuredEnd} existingDraftTaskCount={ExistingDraftTaskCount} existingSourceTaskCount={ExistingSourceTaskCount} generatedTaskCount={GeneratedTaskCount} seededTaskCount={SeededTaskCount}",
                draftPlan.Id,
                requestedRangeStart,
                requestedRangeEnd,
                ensuredStart,
                ensuredEnd,
                draftPlan.Tasks.Count,
                existingSourceTaskIds.Count,
                generatedTasks.Count,
                seededTasks.Count);

            if (seededTasks.Count > 0)
            {
                _db.Tasks.AddRange(seededTasks);
                await _db.SaveChangesAsync(cancellationToken);
            }

            var currentTaskSpan = await _db.Tasks
                .Where(t => t.PlanId == draftPlan.Id)
                .Select(t => new { Start = t.StartAt.Date, End = t.EndAt.Date })
                .ToListAsync(cancellationToken);

            draftPlan.PeriodStart = currentTaskSpan.Any()
                ? new[] { ensuredStart, currentTaskSpan.Min(t => t.Start) }.Min()
                : ensuredStart;
            draftPlan.PeriodEnd = currentTaskSpan.Any()
                ? new[] { ensuredEnd, currentTaskSpan.Max(t => t.End) }.Max()
                : ensuredEnd;
            var totalDays = (draftPlan.PeriodEnd - draftPlan.PeriodStart).Days + 1;
            draftPlan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));

            _db.Plans.Update(draftPlan);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await _evaluationEngine.RecalculatePlanAsync(draftPlan.Id, cancellationToken);

            _logger.LogInformation(
                "svc.plan.backlog-draft.ensure planId={PlanId} requestedStart={RequestedStart} requestedEnd={RequestedEnd} seededTasks={SeededTasks} start={Start} end={End}",
                draftPlan.Id,
                requestedRangeStart,
                requestedRangeEnd,
                seededTasks.Count,
                draftPlan.PeriodStart,
                draftPlan.PeriodEnd);

            return draftPlan;
        }

        private PlanningWindow BuildBacklogDraftWindow(
            DateTime? requestedStart,
            DateTime? requestedEnd,
            int? requestedWeeks,
            int fallbackWeeks)
        {
            var today = _timeService.GetLocalToday().Date;
            var requestedStartDate = requestedStart?.Date;
            var requestedEndDate = requestedEnd?.Date;

            if ((requestedStartDate.HasValue && requestedStartDate.Value < today) ||
                (requestedEndDate.HasValue && requestedEndDate.Value < today))
            {
                throw new InvalidOperationException($"Drafts may start from {today:yyyy-MM-dd}.");
            }

            var startDate = requestedStartDate ?? today;
            if (startDate < today)
            {
                startDate = today;
            }

            var requestedSpanDays = requestedStartDate.HasValue &&
                                    requestedEndDate.HasValue &&
                                    requestedEndDate.Value >= requestedStartDate.Value
                ? (requestedEndDate.Value - requestedStartDate.Value).Days + 1
                : 0;

            var plannedSpanDays = requestedSpanDays > 0
                ? requestedSpanDays
                : requestedWeeks.HasValue && requestedWeeks.Value > 0
                    ? requestedWeeks.Value * 7
                    : fallbackWeeks * 7;

            if (plannedSpanDays < 1)
            {
                plannedSpanDays = 1;
            }

            var endDate = startDate.AddDays(plannedSpanDays - 1);
            var weeks = Math.Max(1, (int)Math.Ceiling(plannedSpanDays / 7.0));

            _logger.LogInformation(
                "svc.plan.backlog-draft.window start={Start} end={End} days={Days} weeks={Weeks} today={Today} requestedStart={RequestedStart} requestedEnd={RequestedEnd}",
                startDate,
                endDate,
                plannedSpanDays,
                weeks,
                today,
                requestedStartDate,
                requestedEndDate);

            return new PlanningWindow(startDate, endDate, plannedSpanDays, weeks, today);
        }

        internal async Task<PlanEntity> GetOrExtendActivePlanAsync(PlanEntity? activePlan, DateTime taskDate, DateTime userLocalToday, CancellationToken cancellationToken)
        {
            if (activePlan != null)
            {
                // Extend Active plan to cover the task date
                if (taskDate > activePlan.PeriodEnd.Date)
                {
                    activePlan.PeriodEnd = taskDate;
                    var totalDays = (activePlan.PeriodEnd - activePlan.PeriodStart).Days + 1;
                    activePlan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));
                    _db.Plans.Update(activePlan);
            await _db.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("svc.plan.extend-active planId={PlanId} newEnd={NewEnd}", activePlan.Id, activePlan.PeriodEnd);
                    return activePlan;
                }
                else if (taskDate < activePlan.PeriodStart.Date)
                {
                    activePlan.PeriodStart = taskDate;
                    var totalDays = (activePlan.PeriodEnd - activePlan.PeriodStart).Days + 1;
                    activePlan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));
                    _db.Plans.Update(activePlan);
            await _db.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("svc.plan.extend-active planId={PlanId} newStart={NewStart}", activePlan.Id, activePlan.PeriodStart);
                    return activePlan;
                }
                return activePlan;
            }
            else
            {
                // Create new Active plan
                var newActivePlan = new PlanEntity
                {
                    PeriodStart = userLocalToday,
                    PeriodEnd = taskDate > userLocalToday ? taskDate : userLocalToday.AddDays(27), // Default 4 weeks
                    Weeks = 4,
                    Status = PlanStatus.Active,
                    CreatedAt = DateTime.UtcNow
                };
                var totalDays = (newActivePlan.PeriodEnd - newActivePlan.PeriodStart).Days + 1;
                newActivePlan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));
                _db.Plans.Add(newActivePlan);
            await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("svc.plan.create-active planId={PlanId} start={Start} end={End}", newActivePlan.Id, newActivePlan.PeriodStart, newActivePlan.PeriodEnd);
                return newActivePlan;
            }
        }

        internal async Task AdjustPlanSpanAsync(PlanEntity plan, CancellationToken cancellationToken)
        {
            var span = await _db.Tasks
                .Where(t => t.PlanId == plan.Id)
                .Select(t => new { Start = t.StartAt.Date, End = t.EndAt.Date })
                .ToListAsync(cancellationToken);

            if (!span.Any())
            {
                return;
            }

            var min = span.Min(t => t.Start);
            var max = span.Max(t => t.End);
            var totalDays = (max - min).Days + 1;

            plan.PeriodStart = min;
            plan.PeriodEnd = max;
            plan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));

            _db.Plans.Update(plan);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("svc.plan.span.adjust planId={PlanId} start={Start} end={End} weeks={Weeks}", plan.Id, plan.PeriodStart, plan.PeriodEnd, plan.Weeks);
        }

        public async Task ApplyDraftToActiveAsync(CancellationToken cancellationToken = default)
        {
            var draft = await _db.Plans
                .Include(p => p.Tasks)
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);

            if (draft == null)
            {
                throw new InvalidOperationException("No draft plan to publish.");
            }

            var active = await _db.Plans
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);

            await EnsureDraftEvaluationsAsync(draft, cancellationToken);

            var nowUtc = _timeService.GetCurrentUtcTime();
            var replacementStartDate = draft.PeriodStart.Date;
            var currentLocalToday = _timeService.GetLocalToday();
            if (replacementStartDate < currentLocalToday)
            {
                replacementStartDate = currentLocalToday;
            }

            var replacementStartUtc = DateTime.SpecifyKind(replacementStartDate, DateTimeKind.Utc);
            var replacementEndUtcExclusive = DateTime.SpecifyKind(draft.PeriodEnd.Date.AddDays(1), DateTimeKind.Utc);

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            var deletedActiveTasks = 0;
            PlanEntity targetActivePlan;
            if (active != null)
            {
                targetActivePlan = active;
                deletedActiveTasks = await _db.Tasks
                    .Where(t => t.PlanId == active.Id)
                    .Where(t => t.StartAt < replacementEndUtcExclusive && t.EndAt > replacementStartUtc)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            else
            {
                targetActivePlan = new PlanEntity
                {
                    PeriodStart = draft.PeriodStart,
                    PeriodEnd = draft.PeriodEnd,
                    Weeks = draft.Weeks,
                    Status = PlanStatus.Active,
                    CreatedAt = _timeService.GetCurrentUtcTime(),
                    CreatedBy = draft.CreatedBy
                };

                _db.Plans.Add(targetActivePlan);
                await _db.SaveChangesAsync(cancellationToken);
            }

            var movedDraftTasks = await _db.Tasks
                .Where(t => t.PlanId == draft.Id)
                .Where(t => t.StartAt < replacementEndUtcExclusive && t.EndAt > replacementStartUtc)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(t => t.PlanId, targetActivePlan.Id)
                        .SetProperty(t => t.SourceTaskId, (long?)null),
                    cancellationToken);

            targetActivePlan.PeriodStart = draft.PeriodStart;
            targetActivePlan.PeriodEnd = draft.PeriodEnd;
            targetActivePlan.Weeks = draft.Weeks;
            _db.Plans.Update(targetActivePlan);

            var deletedOutOfWindowDraftTasks = await _db.Tasks
                .Where(t => t.PlanId == draft.Id)
                .ExecuteDeleteAsync(cancellationToken);

            var today = _timeService.GetLocalToday();
            draft.PeriodStart = today;
            draft.PeriodEnd = today;
            draft.Weeks = 1;
            _db.Plans.Update(draft);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "svc.plan.draft-apply.ok draftId={DraftId} activeId={ActiveId} deletedActiveTasks={DeletedActiveTasks} movedDraftTasks={MovedDraftTasks} deletedRemainingDraftTasks={DeletedRemainingDraftTasks} replacementStartUtc={ReplacementStartUtc} replacementEndUtcExclusive={ReplacementEndUtcExclusive} nowUtc={NowUtc}",
                draft.Id,
                targetActivePlan.Id,
                deletedActiveTasks,
                movedDraftTasks,
                deletedOutOfWindowDraftTasks,
                replacementStartUtc,
                replacementEndUtcExclusive,
                nowUtc);
        }

        public async Task DiscardDraftAsync(CancellationToken cancellationToken = default)
        {
            var draft = await _db.Plans
                .Include(p => p.Tasks)
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);

            if (draft == null)
            {
                return;
            }

            await _evaluationEngine.RecalculatePlanAsync(draft.Id, cancellationToken);

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            if (draft.Tasks.Any())
            {
                _db.Tasks.RemoveRange(draft.Tasks);
            }

            var today = _timeService.GetLocalToday();
            draft.PeriodStart = today;
            draft.PeriodEnd = today;
            draft.Weeks = 1;
            _db.Plans.Update(draft);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation("svc.plan.discard draftId={DraftId} removedTasks={Tasks} preservedDraftPlan=true", draft.Id, draft.Tasks.Count);
        }

        public async Task<(long? ActivePlanId, long? DraftPlanId)> RecalculateCurrentPlansAsync(CancellationToken cancellationToken = default)
        {
            var active = await GetActivePlanAsync(cancellationToken);
            var draft = await GetDraftPlanAsync(cancellationToken);

            if (active == null && draft == null)
            {
                _logger.LogInformation("svc.plan.recalculate-current.none");
                return (null, null);
            }

            if (active != null)
            {
                await _evaluationEngine.RecalculatePlanAsync(active.Id, cancellationToken);
            }

            if (draft != null)
            {
                await _evaluationEngine.RecalculatePlanAsync(draft.Id, cancellationToken);
            }

            _logger.LogInformation("svc.plan.recalculate-current activeId={ActivePlanId} draftId={DraftPlanId}", active?.Id, draft?.Id);
            return (active?.Id, draft?.Id);
        }

        private async Task EnsureDraftEvaluationsAsync(PlanEntity draft, CancellationToken cancellationToken)
        {
            try
            {
                await _evaluationEngine.RecalculatePlanAsync(draft.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recalculate evaluations for draft plan {PlanId} during publish.", draft.Id);
                throw;
            }

            var draftTasksCount = draft.Tasks.Count;
            var draftTaskIds = draft.Tasks.Select(t => t.Id).ToList();
            var draftEvalCount = draftTaskIds.Count == 0
                ? 0
                : await _db.TaskEvaluations.CountAsync(e => draftTaskIds.Contains(e.TaskId), cancellationToken);

            if (draftTasksCount > 0 && draftEvalCount < draftTasksCount)
            {
                await _evaluationEngine.RecalculatePlanAsync(draft.Id, cancellationToken);
                draftEvalCount = await _db.TaskEvaluations.CountAsync(e => draftTaskIds.Contains(e.TaskId), cancellationToken);
                if (draftEvalCount < draftTasksCount)
                {
                    _logger.LogWarning("Draft plan {PlanId} has {EvalCount}/{TaskCount} evaluations; proceeding with publish.", draft.Id, draftEvalCount, draftTasksCount);
                }
            }
        }

        /// <summary>
        /// Seeds draft-plan tasks from the active plan for draft scheduling flows.
        /// Shared by the backlog draft sandbox and any other draft seeding path.
        /// </summary>
        private async Task<List<TaskEntity>> GenerateTasksFromActivePlanAsync(
            PlanEntity activePlan,
            DateTime draftStart,
            DateTime draftEnd,
            long draftPlanId,
            CancellationToken cancellationToken)
        {
            var result = new List<TaskEntity>();
            var nowUtc = _timeService.GetCurrentUtcTime();
            var draftStartUtc = DateTime.SpecifyKind(draftStart.Date, DateTimeKind.Utc);
            var draftEndUtcExclusive = DateTime.SpecifyKind(draftEnd.Date.AddDays(1), DateTimeKind.Utc);

            _logger.LogInformation(
                "svc.plan.draft.clone.start activePlanId={ActivePlanId} draftStart={DraftStart} draftEnd={DraftEnd} activePeriod={ActiveStart}-{ActiveEnd}",
                activePlan.Id, draftStart, draftEnd, activePlan.PeriodStart, activePlan.PeriodEnd);

            // Backlog sandbox should clone only current/future tasks that are actually
            // in the requested working window, not repeat historical patterns.
            var activeTasks = await _db.Tasks
                .Include(t => t.ServiceObject)
                .Where(t => t.PlanId == activePlan.Id)
                .Where(t => t.EndAt >= nowUtc)
                .Where(t => t.StartAt < draftEndUtcExclusive && t.EndAt > draftStartUtc)
                .OrderBy(t => t.StartAt)
                .ToListAsync(cancellationToken);

            if (!activeTasks.Any())
            {
                _logger.LogWarning("svc.plan.draft.clone no tasks in active plan activePlanId={PlanId}", activePlan.Id);
                return result;
            }

            _logger.LogInformation(
                "svc.plan.draft.clone.tasks-loaded count={TaskCount} firstTask={FirstStart} lastTask={LastEnd}",
                activeTasks.Count, activeTasks.First().StartAt, activeTasks.Last().EndAt);

            foreach (var sourceTask in activeTasks)
            {
                var newTask = new TaskEntity
                {
                    PlanId = draftPlanId,
                    TechnicianId = sourceTask.TechnicianId,
                    ServiceObjectId = sourceTask.ServiceObjectId,
                    Title = sourceTask.Title,
                    StartAt = sourceTask.StartAt,
                    EndAt = sourceTask.EndAt,
                    Priority = sourceTask.Priority,
                    Status = sourceTask.Status,
                    SkillLevel = sourceTask.SkillLevel,
                    Instructions = sourceTask.Instructions,
                    TechnicianComment = string.Empty,
                    SourceTaskId = sourceTask.Id,
                    SourceBacklogItemId = sourceTask.SourceBacklogItemId,
                    CreatedAt = DateTime.UtcNow
                };

                result.Add(newTask);
            }

            _logger.LogInformation(
                "svc.plan.draft.clone generated {TaskCount} tasks from {SourceTaskCount} source tasks, activePlanId={ActivePlanId} nowUtc={NowUtc}",
                result.Count, activeTasks.Count, activePlan.Id, nowUtc);

            return result;
        }
    }
}
