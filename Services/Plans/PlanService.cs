using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.Plans
{
    public class PlanService : IPlanService
    {
        private readonly ScheduleDbContext _db;
        private readonly ITaskEvaluationService _taskEvaluationService;
        private readonly ILogger<PlanService> _logger;

        public PlanService(ScheduleDbContext db, ITaskEvaluationService taskEvaluationService, ILogger<PlanService> logger)
        {
            _db = db;
            _taskEvaluationService = taskEvaluationService;
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

            // Determine the latest planned/task end across all plans to avoid overlaps.
            var lastPlannedEnd = await _db.Plans.MaxAsync(p => p.PeriodEnd, cancellationToken);
            var lastTaskEnd = await _db.Tasks.MaxAsync(t => (DateTime?)t.EndAt, cancellationToken) ?? lastPlannedEnd;
            if (lastTaskEnd.Date > lastPlannedEnd.Date)
            {
                lastPlannedEnd = lastTaskEnd.Date;
            }

            var technicians = await _db.Technicians
                .Include(t => t.Region)
                .ToListAsync(cancellationToken);

            var firstFreeDay = lastPlannedEnd.Date.AddDays(1);
            // Clamp requested start to the first free day after the last existing plan to avoid overlap.
            var startDate = requestedStart.HasValue && requestedStart.Value.Date > firstFreeDay
                ? requestedStart.Value.Date
                : firstFreeDay;
            DateTime endDate;
            if (requestedEnd.HasValue && requestedEnd.Value.Date >= startDate)
            {
                endDate = requestedEnd.Value.Date;
            }
            else if (requestedWeeks.HasValue && requestedWeeks.Value > 0)
            {
                endDate = startDate.AddDays(requestedWeeks.Value * 7 - 1);
            }
            else
            {
                var fallbackWeeks = activePlan.Weeks > 0 ? activePlan.Weeks : 4;
                endDate = startDate.AddDays(fallbackWeeks * 7 - 1);
            }

            var totalDays = (endDate - startDate).Days + 1;
            var weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                _logger.LogInformation("CreateDraft: requestedStart={RequestedStart}, requestedEnd={RequestedEnd}, clampedStart={StartDate}, endDate={EndDate}, weeks={Weeks}, activePlanId={ActivePlanId}, activeStart={ActiveStart}, activeEnd={ActiveEnd}, lastPlannedEnd={LastPlannedEnd}", requestedStart?.Date, requestedEnd?.Date, startDate, endDate, weeks, activePlan.Id, activePlan.PeriodStart, activePlan.PeriodEnd, lastPlannedEnd);
            }
            catch
            {
                // logging only
            }

            var draftPlan = new PlanEntity
            {
                PeriodStart = startDate,
                PeriodEnd = endDate,
                Weeks = weeks,
                Status = PlanStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = activePlan.CreatedBy
            };

            _db.Plans.Add(draftPlan);
            await _db.SaveChangesAsync(cancellationToken);

            var seededTasks = GenerateTasksFromActivePlan(activePlan, technicians, startDate, endDate, draftPlan.Id);
            if (seededTasks.Any())
            {
                _db.Tasks.AddRange(seededTasks);
                await _db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            await _taskEvaluationService.RecalculatePlanAsync(draftPlan.Id, cancellationToken);
            return draftPlan;
        }

        public async Task<TaskEntity> UpdateTaskAsync(long taskId, Action<TaskEntity> applyChanges, CancellationToken cancellationToken = default)
        {
            var draftExists = await _db.Plans.AnyAsync(p => p.Status == PlanStatus.Draft, cancellationToken);

            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                throw new InvalidOperationException($"Task {taskId} was not found.");
            }

            if (task.Plan?.Status != PlanStatus.Draft)
            {
                throw new InvalidOperationException("Active plan is read-only while a draft exists.");
            }

            applyChanges(task);
            await _db.SaveChangesAsync(cancellationToken);
            if (task.PlanId.HasValue)
            {
                await _taskEvaluationService.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
            }
            return task;
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
                await _taskEvaluationService.RecalculatePlanAsync(draft.Id, cancellationToken);
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
                await _taskEvaluationService.RecalculatePlanAsync(draft.Id, cancellationToken);
                draftEvalCount = await _db.TaskEvaluations.CountAsync(e => draftTaskIds.Contains(e.TaskId), cancellationToken);
                if (draftEvalCount < draftTasksCount)
                {
                    _logger.LogWarning("Draft plan {PlanId} has {EvalCount}/{TaskCount} evaluations; proceeding with publish.", draft.Id, draftEvalCount, draftTasksCount);
                }
            }

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                _logger.LogInformation("PublishDraft: draftId={DraftId}, draftPeriod={DraftStart}-{DraftEnd}, draftTasks={DraftTasks}, draftEvals={DraftEvals}, activeId={ActiveId}", draft.Id, draft.PeriodStart, draft.PeriodEnd, draftTasksCount, draftEvalCount, active?.Id);
            }
            catch
            {
                // logging only
            }

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

            await _taskEvaluationService.RecalculatePlanAsync(draft.Id, cancellationToken);

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            if (draft.Tasks.Any())
            {
                _db.Tasks.RemoveRange(draft.Tasks);
            }

            _db.Plans.Remove(draft);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        private static IEnumerable<TaskEntity> GenerateTasksFromActivePlan(PlanEntity activePlan, IEnumerable<TechnicianEntity> technicians, DateTime startDate, DateTime endDate, long planId)
        {
            var start = startDate.Date;
            var end = endDate.Date;
            if (end < start)
            {
                yield break;
            }

            var techLookup = technicians.ToDictionary(t => t.Id);
            var tasksByTech = activePlan.Tasks.GroupBy(t => t.TechnicianId).ToDictionary(g => g.Key, g => g.ToList());

            for (var day = start; day <= end; day = day.AddDays(1))
            {
                foreach (var kvp in tasksByTech)
                {
                    if (!techLookup.TryGetValue(kvp.Key, out var tech))
                    {
                        continue;
                    }

                    var dailyLimit = tech.DailyLimit > 0 ? (double)tech.DailyLimit : 8.0;
                    var sourceTasks = kvp.Value
    .Where(t => t.StartAt.DayOfWeek == day.DayOfWeek)
    .GroupBy(t => new
    {
        Start = t.StartAt.TimeOfDay,
        End = t.EndAt.TimeOfDay,
        t.Title,
        t.Location,
        t.Priority,
        RegionId = t.RegionId ?? tech.RegionId
    })
    .Select(g => g.OrderBy(x => x.StartAt).First())
    .OrderBy(t => t.StartAt.TimeOfDay)
    .ToList();

                    if (!sourceTasks.Any())
                    {
                        continue;
                    }

                    var accumulatedHours = 0.0;
                    foreach (var src in sourceTasks)
                    {
                        var duration = (src.EndAt - src.StartAt).TotalHours;
                        if (accumulatedHours + duration > dailyLimit)
                        {
                            break;
                        }

                        var startAt = DateTime.SpecifyKind(day + src.StartAt.TimeOfDay, DateTimeKind.Utc);
                        var endAt = DateTime.SpecifyKind(day + src.EndAt.TimeOfDay, DateTimeKind.Utc);

                        yield return new TaskEntity
                        {
                            PlanId = planId,
                            TechnicianId = tech.Id,
                            Title = src.Title,
                            Location = src.Location,
                            RegionId = src.RegionId ?? tech.RegionId,
                            StartAt = startAt,
                            EndAt = endAt,
                            Priority = src.Priority,
                            Status = ScheduleTaskStatus.Planned,
                            CreatedAt = DateTime.UtcNow
                        };

                        accumulatedHours += duration;
                    }
                }
            }
        }
    }
}
