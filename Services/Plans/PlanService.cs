using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Data.Seed;
using ServiceTaskPlanningSystem.Services.Planning;
using ServiceTaskPlanningSystem.Services.Evaluation;

namespace ServiceTaskPlanningSystem.Services.Plans
{
    public class PlanService : IPlanService
    {
        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly IPlanningCalendar _planningCalendar;
        private readonly ILogger<PlanService> _logger;

        public PlanService(ScheduleDbContext db, IEvaluationEngine evaluationEngine, IPlanningCalendar planningCalendar, ILogger<PlanService> logger)
        {
            _db = db;
            _evaluationEngine = evaluationEngine;
            _planningCalendar = planningCalendar;
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

            var technicians = await _db.Technicians
                .Include(t => t.Region)
                .ToListAsync(cancellationToken);

            var fallbackWeeks = activePlan.Weeks > 0 ? activePlan.Weeks : 4;
            var window = await _planningCalendar.BuildWindowAsync(requestedStart, requestedEnd, requestedWeeks, fallbackWeeks, cancellationToken);

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            _logger.LogInformation("svc.plan.draft.create requestedStart={RequestedStart} requestedEnd={RequestedEnd} start={StartDate} end={EndDate} weeks={Weeks} activePlanId={ActivePlanId}", requestedStart?.Date, requestedEnd?.Date, window.Start, window.End, window.Weeks, activePlan.Id);

            var draftPlan = new PlanEntity
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

            var seededTasks = GenerateTasksFromTemplate(technicians, window.Start, window.End, draftPlan.Id);
            if (seededTasks.Any())
            {
                _db.Tasks.AddRange(seededTasks);
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
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
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

        private static IEnumerable<TaskEntity> GenerateTasksFromTemplate(IEnumerable<TechnicianEntity> technicians, DateTime startDate, DateTime endDate, long planId)
        {
            var start = startDate.Date;
            var end = endDate.Date;
            if (end < start)
            {
                yield break;
            }

            var techLookup = technicians.ToDictionary(t => t.Id);
            var templateWeek = SeedTemplateProvider.WeekTemplate;
            var tasksByTech = templateWeek
                .GroupBy(t => t.TechnicianId)
                .ToDictionary(g => g.Key, g => g.ToList());

            for (var day = start; day <= end; day = day.AddDays(1))
            {
                var templateDayOfWeek = day.DayOfWeek; // weekends naturally skipped if template has none
                if (!templateWeek.Any(t => t.Start.Date.DayOfWeek == templateDayOfWeek))
                {
                    continue;
                }

                foreach (var kvp in tasksByTech)
                {
                    if (!techLookup.TryGetValue(kvp.Key, out var tech))
                    {
                        continue;
                    }

                    var dailyLimit = tech.DailyLimit > 0 ? (double)tech.DailyLimit : 8.0;
                    var sourceTasks = kvp.Value
                        .Where(t => t.Start.Date.DayOfWeek == templateDayOfWeek)
                        .GroupBy(t => new
                        {
                            Start = t.Start.TimeOfDay,
                            End = t.End.TimeOfDay,
                            t.Title,
                            t.Location,
                            t.Priority
                        })
                        .Select(g => g.OrderBy(x => x.Start).First())
                        .OrderBy(t => t.Start.TimeOfDay)
                        .ToList();

                    if (!sourceTasks.Any())
                    {
                        continue;
                    }

                    var accumulatedHours = 0.0;
                    foreach (var src in sourceTasks)
                    {
                        var duration = (src.End - src.Start).TotalHours;
                        if (accumulatedHours + duration > dailyLimit)
                        {
                            break;
                        }

                        var startAt = DateTime.SpecifyKind(day + src.Start.TimeOfDay, DateTimeKind.Utc);
                        var endAt = DateTime.SpecifyKind(day + src.End.TimeOfDay, DateTimeKind.Utc);

                        yield return new TaskEntity
                        {
                            PlanId = planId,
                            TechnicianId = tech.Id,
                            Title = src.Title,
                            Location = src.Location,
                            RegionId = tech.RegionId,
                            StartAt = startAt,
                            EndAt = endAt,
                            Priority = MapPriority(src.Priority, src.Risk),
                            Status = ScheduleTaskStatus.Planned,
                            CreatedAt = DateTime.UtcNow
                        };

                        accumulatedHours += duration;
                    }
                }
            }
        }

        private static TaskPriority MapPriority(string? priority, string? risk)
        {
            if (!string.IsNullOrWhiteSpace(priority))
            {
                return priority.ToLowerInvariant() switch
                {
                    "aukstas" => TaskPriority.High,
                    "high" => TaskPriority.High,
                    "zemas" => TaskPriority.Low,
                    "low" => TaskPriority.Low,
                    _ => TaskPriority.Medium
                };
            }

            return MapRiskLevel(risk) switch
            {
                RiskLevel.High => TaskPriority.High,
                RiskLevel.Low => TaskPriority.Low,
                _ => TaskPriority.Medium
            };
        }

        private static RiskLevel? MapRiskLevel(string? risk)
        {
            if (string.IsNullOrWhiteSpace(risk)) return null;
            return risk.ToLowerInvariant() switch
            {
                "aukstas" => RiskLevel.High,
                "high" => RiskLevel.High,
                "zemas" => RiskLevel.Low,
                "low" => RiskLevel.Low,
                _ => RiskLevel.Medium
            };
        }
    }
}
