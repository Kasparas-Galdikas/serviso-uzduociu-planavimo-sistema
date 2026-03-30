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

        public async Task<PlanEntity> CreateDraftFromActiveAsync(DateTime? requestedStart = null, DateTime? requestedEnd = null, int? requestedWeeks = null, CancellationToken cancellationToken = default)
        {
            var existingDraft = await GetDraftPlanAsync(cancellationToken);
            if (existingDraft != null)
            {
                throw new InvalidOperationException("Draft already exists. Cannot create another draft.");
            }

            var activePlan = await _db.Plans
                .Include(p => p.Tasks)
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);

            if (activePlan == null)
            {
                throw new InvalidOperationException("No active plan found to clone.");
            }

            var technicians = await _db.Technicians.ToListAsync(cancellationToken);

            var fallbackWeeks = activePlan.Weeks > 0 ? activePlan.Weeks : 4;
            var window = await _planningCalendar.BuildWindowAsync(requestedStart, requestedEnd, requestedWeeks, fallbackWeeks, cancellationToken);

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            _logger.LogInformation("svc.plan.draft.create requestedStart={RequestedStart} requestedEnd={RequestedEnd} start={StartDate} end={EndDate} weeks={Weeks} activePlanId={ActivePlanId}", requestedStart?.Date, requestedEnd?.Date, window.Start, window.End, window.Weeks, activePlan.Id);

            var draftPlan = new PlanEntity
            {
                PeriodStart = window.Start,
                PeriodEnd = window.End, // will be set to actual task span after seeding
                Weeks = window.Weeks,
                Status = PlanStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = activePlan.CreatedBy
            };

            _db.Plans.Add(draftPlan);
            await _db.SaveChangesAsync(cancellationToken);

            var generationEnd = window.End;
            var seededTasks = await GenerateTasksFromActivePlanAsync(activePlan, window.Start, generationEnd, draftPlan.Id, cancellationToken);
            if (seededTasks.Any())
            {
                _db.Tasks.AddRange(seededTasks);
            await _db.SaveChangesAsync(cancellationToken);

                // Align PeriodStart and PeriodEnd to actual task span (true inclusive horizon)
                var actualStart = seededTasks.Min(t => t.StartAt.Date);
                var actualEnd = seededTasks.Max(t => t.EndAt.Date);

                _logger.LogInformation("svc.plan.draft.task-span windowStart={WindowStart} windowEnd={WindowEnd} actualStart={ActualStart} actualEnd={ActualEnd}",
                    window.Start, window.End, actualStart, actualEnd);

                draftPlan.PeriodStart = actualStart;
                draftPlan.PeriodEnd = actualEnd;
                var totalDays = (draftPlan.PeriodEnd - draftPlan.PeriodStart).Days + 1;
                draftPlan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));
                _db.Plans.Update(draftPlan);
            await _db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                _logger.LogWarning("svc.plan.draft.seed.empty planId={PlanId} start={Start} end={End}", draftPlan.Id, window.Start, window.End);
            }

            await transaction.CommitAsync(cancellationToken);
            await _evaluationEngine.RecalculatePlanAsync(draftPlan.Id, cancellationToken);
            _logger.LogInformation("svc.plan.draft.created planId={PlanId} tasksSeeded={TaskCount}", draftPlan.Id, seededTasks.Count());
            return draftPlan;
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

        public async Task PublishDraftAsync(CancellationToken cancellationToken = default)
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

            // Validation hook: place draft validation here before flipping states.

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
                // Try one more recalculation in case evaluations were stale.
                await _evaluationEngine.RecalculatePlanAsync(draft.Id, cancellationToken);
                draftEvalCount = await _db.TaskEvaluations.CountAsync(e => draftTaskIds.Contains(e.TaskId), cancellationToken);
                if (draftEvalCount < draftTasksCount)
                {
                    _logger.LogWarning("Draft plan {PlanId} has {EvalCount}/{TaskCount} evaluations; proceeding with publish.", draft.Id, draftEvalCount, draftTasksCount);
                }
            }

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            _logger.LogInformation("svc.plan.publish draftId={DraftId} draftTasks={DraftTasks} draftEvals={DraftEvals} activeId={ActiveId}", draft.Id, draftTasksCount, draftEvalCount, active?.Id);

            if (active != null)
            {
                active.Status = PlanStatus.Superseded;
                active.SupersededByPlanId = draft.Id;
                _db.Plans.Update(active);
            }

            draft.Status = PlanStatus.Active;
            _db.Plans.Update(draft);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation("svc.plan.publish.ok draftId={DraftId} superseded={ActiveId}", draft.Id, active?.Id);
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

            _db.Plans.Remove(draft);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation("svc.plan.discard draftId={DraftId} removedTasks={Tasks}", draft.Id, draft.Tasks.Count);
        }

        /// <summary>
        /// Generates tasks for a draft plan by cloning tasks from the active plan.
        /// Tasks are adjusted to fit the new date range while preserving all relationships.
        /// </summary>
        private async Task<List<TaskEntity>> GenerateTasksFromActivePlanAsync(
            PlanEntity activePlan,
            DateTime draftStart,
            DateTime draftEnd,
            long draftPlanId,
            CancellationToken cancellationToken)
        {
            var result = new List<TaskEntity>();

            _logger.LogInformation(
                "svc.plan.draft.clone.start activePlanId={ActivePlanId} draftStart={DraftStart} draftEnd={DraftEnd} activePeriod={ActiveStart}-{ActiveEnd}",
                activePlan.Id, draftStart, draftEnd, activePlan.PeriodStart, activePlan.PeriodEnd);

            // Load active plan tasks with all relationships
            var activeTasks = await _db.Tasks
                .Include(t => t.ServiceObject)
                .Where(t => t.PlanId == activePlan.Id)
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

            // Calculate how many days the active task span covers (robust even if plan metadata is stale)
            var sourceStart = activeTasks.Min(t => t.StartAt.Date);
            var sourceEnd = activeTasks.Max(t => t.EndAt.Date);
            var sourceSpanDays = (sourceEnd - sourceStart).Days + 1;
            var draftDays = (draftEnd.Date - draftStart.Date).Days + 1;

            _logger.LogInformation(
                "svc.plan.draft.clone.dates sourceSpanDays={SourceDays} draftDays={DraftDays} sourceStart={SourceStart} sourceEnd={SourceEnd}",
                sourceSpanDays, draftDays, sourceStart, sourceEnd);

            // Group tasks by day offset from plan start
            var tasksByDayOffset = activeTasks
                .GroupBy(t => (t.StartAt.Date - sourceStart).Days)
                .ToDictionary(g => g.Key, g => g.ToList());

            _logger.LogInformation(
                "svc.plan.draft.clone.grouped dayOffsets={DayOffsets} totalTasksGrouped={TotalTasks}",
                string.Join(",", tasksByDayOffset.Keys.OrderBy(k => k)),
                tasksByDayOffset.Sum(kvp => kvp.Value.Count));

            // Clone tasks for each day in the draft period
            for (var dayOffset = 0; dayOffset < draftDays; dayOffset++)
            {
                var targetDate = draftStart.Date.AddDays(dayOffset);

                // Use modulo to cycle through the active plan's task pattern
                var sourceDayOffset = dayOffset % sourceSpanDays;

                if (!tasksByDayOffset.TryGetValue(sourceDayOffset, out var sourceTasks))
                {
                    _logger.LogDebug(
                        "svc.plan.draft.clone.skip-day dayOffset={DayOffset} targetDate={TargetDate} sourceDayOffset={SourceDayOffset} reason=no-source-tasks",
                        dayOffset, targetDate, sourceDayOffset);
                    continue; // No tasks for this day of week pattern
                }

                _logger.LogDebug(
                    "svc.plan.draft.clone.day dayOffset={DayOffset} targetDate={TargetDate} sourceDayOffset={SourceDayOffset} sourceTaskCount={Count}",
                    dayOffset, targetDate, sourceDayOffset, sourceTasks.Count);

                foreach (var sourceTask in sourceTasks)
                {
                    // Calculate time offset from the source day
                    var timeOfDay = sourceTask.StartAt.TimeOfDay;
                    var duration = sourceTask.EndAt - sourceTask.StartAt;

                    // Create new task with adjusted dates
                    var newTask = new TaskEntity
                    {
                        PlanId = draftPlanId,
                        TechnicianId = sourceTask.TechnicianId,
                        ServiceObjectId = sourceTask.ServiceObjectId, // Preserve service object reference
                        Title = sourceTask.Title,
                        StartAt = DateTime.SpecifyKind(targetDate.Add(timeOfDay), DateTimeKind.Utc),
                        EndAt = DateTime.SpecifyKind(targetDate.Add(timeOfDay).Add(duration), DateTimeKind.Utc),
                        Priority = sourceTask.Priority,
                        Status = ScheduleTaskStatus.InProgress,
                        SkillLevel = sourceTask.SkillLevel,
                        Instructions = sourceTask.Instructions,
                        TechnicianComment = string.Empty, // Clear comment for new plan
                        CreatedAt = DateTime.UtcNow
                    };

                    result.Add(newTask);
                }
            }

            _logger.LogInformation(
                "svc.plan.draft.clone generated {TaskCount} tasks from {SourceTaskCount} source tasks, activePlanId={ActivePlanId}",
                result.Count, activeTasks.Count, activePlan.Id);

            return result;
        }
    }
}
