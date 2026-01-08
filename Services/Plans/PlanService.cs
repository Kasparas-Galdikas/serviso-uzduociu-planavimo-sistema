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
using ServiceTaskPlanningSystem.Services.Schedule;
using ServiceTaskPlanningSystem.Services.Evaluation;
using ServiceTaskPlanningSystem.Services.Authorization;

namespace ServiceTaskPlanningSystem.Services.Plans
{
    public class PlanService : IPlanService
    {
        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly IPlanningCalendar _planningCalendar;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly ILogger<PlanService> _logger;

        public PlanService(ScheduleDbContext db, IEvaluationEngine evaluationEngine, IPlanningCalendar planningCalendar, ITaskPermissionService taskPermissionService, ILogger<PlanService> logger)
        {
            _db = db;
            _evaluationEngine = evaluationEngine;
            _planningCalendar = planningCalendar;
            _taskPermissionService = taskPermissionService;
            _logger = logger;
        }

        private sealed record WorkingWindow(int StartMinutes, int EndMinutes);

        private static WorkingWindow GetWorkingWindow(int technicianId, DateTime date) =>
            new WorkingWindow(ScheduleGrid.DayStartMinutes, ScheduleGrid.DayEndMinutes);

        private enum PlacementError
        {
            None,
            InvalidWindow,
            Overlap,
            TechnicianNotFound,
            NotEditable
        }

        private async Task<(PlacementError Error, string Message, DateTime StartAt, DateTime EndAt, long? RegionId)> ValidatePlacementAsync(
            long? ignoreTaskId,
            int targetTechnicianId,
            DateTime targetDate,
            int startMinutes,
            int endMinutes,
            PlanEntity? plan,
            ScheduleTaskStatus status,
            string role,
            bool requireEndAligned,
            CancellationToken cancellationToken)
        {
            if (startMinutes < 0 || startMinutes >= 24 * 60 || endMinutes <= 0 || endMinutes > 24 * 60 || endMinutes <= startMinutes)
            {
                return (PlacementError.InvalidWindow, "Netinkamas laiko langas.", default, default, null);
            }

            if (startMinutes % ScheduleGrid.SlotSizeMinutes != 0)
            {
                return (PlacementError.InvalidWindow, "Laikas turi atitikti grafiko zingsni.", default, default, null);
            }

            if (requireEndAligned && (endMinutes % ScheduleGrid.SlotSizeMinutes != 0))
            {
                return (PlacementError.InvalidWindow, "Pabaigos laikas turi atitikti grafiko zingsni.", default, default, null);
            }

            var window = GetWorkingWindow(targetTechnicianId, targetDate);
            if (startMinutes < window.StartMinutes)
            {
                return (PlacementError.InvalidWindow, "Per ankstyvas laikas.", default, default, null);
            }

            if (endMinutes > window.EndMinutes)
            {
                return (PlacementError.InvalidWindow, "Uzduotis netelpa i darbo laika.", default, default, null);
            }

            var startAt = DateTime.SpecifyKind(targetDate.Date.AddMinutes(startMinutes), DateTimeKind.Utc);
            var endAt = DateTime.SpecifyKind(targetDate.Date.AddMinutes(endMinutes), DateTimeKind.Utc);

            var targetTech = await _db.Technicians
                .AsNoTracking()
                .Select(t => new { t.Id, t.RegionId })
                .FirstOrDefaultAsync(t => t.Id == targetTechnicianId, cancellationToken);

            if (targetTech == null)
            {
                return (PlacementError.TechnicianNotFound, "Technikas nerastas.", default, default, null);
            }

            var permissionProbe = new TaskEntity
            {
                Id = ignoreTaskId ?? 0,
                PlanId = plan?.Id,
                Plan = plan,
                TechnicianId = targetTech.Id,
                RegionId = targetTech.RegionId,
                StartAt = startAt,
                EndAt = endAt,
                Status = status
            };

            var canEdit = await _taskPermissionService.CanEditTaskAsync(permissionProbe, role, cancellationToken);
            if (!canEdit)
            {
                return (PlacementError.NotEditable, "Neturite teisiu atlikti sio veiksmo.", default, default, null);
            }

            var hasOverlap = await _db.Tasks
                .AsNoTracking()
                .Where(t => ignoreTaskId == null || t.Id != ignoreTaskId.Value)
                .Where(t => t.TechnicianId == targetTech.Id)
                .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                .Where(t => t.StartAt < endAt && t.EndAt > startAt)
                .AnyAsync(cancellationToken);

            if (hasOverlap)
            {
                return (PlacementError.Overlap, "Laiko langas uzimtas (persidengia uzduotys).", default, default, null);
            }

            return (PlacementError.None, string.Empty, startAt, endAt, targetTech.RegionId);
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
                PeriodEnd = window.End, // will be set to actual task span after seeding
                Weeks = window.Weeks,
                Status = PlanStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = activePlan.CreatedBy
            };

            _db.Plans.Add(draftPlan);
            await _db.SaveChangesAsync(cancellationToken);

            var generationEnd = window.End;
            var seededTasks = GenerateTasksFromTemplate(technicians, window.Start, generationEnd, draftPlan.Id);
            if (seededTasks.Any())
            {
                _db.Tasks.AddRange(seededTasks);
                await _db.SaveChangesAsync(cancellationToken);

                // Align PeriodEnd to actual task span (true inclusive horizon)
                var actualEnd = seededTasks.Max(t => t.EndAt.Date);
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

        public async Task<TaskCreateResult> CreateTaskAsync(int technicianId, DateTime targetDate, int startMinutes, int endMinutes, string title, string location, TaskPriority priority, ScheduleTaskStatus status, string role, CancellationToken cancellationToken = default)
        {
            var draft = await _db.Plans.FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);
            if (draft == null)
            {
                return new TaskCreateResult(TaskCreateError.DraftNotFound, "Juodrastis nerastas. Uzduotis galima kurti tik juodrascio plane.");
            }

            var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Nauja uzduotis" : title.Trim();
            var normalizedLocation = string.IsNullOrWhiteSpace(location) ? string.Empty : location.Trim();

            var validation = await ValidatePlacementAsync(
                ignoreTaskId: null,
                targetTechnicianId: technicianId,
                targetDate: targetDate,
                startMinutes: startMinutes,
                endMinutes: endMinutes,
                plan: draft,
                status: status,
                role: role,
                requireEndAligned: true,
                cancellationToken: cancellationToken);

            if (validation.Error != PlacementError.None)
            {
                return validation.Error switch
                {
                    PlacementError.NotEditable => new TaskCreateResult(TaskCreateError.NotEditable, validation.Message),
                    PlacementError.TechnicianNotFound => new TaskCreateResult(TaskCreateError.TechnicianNotFound, validation.Message),
                    PlacementError.Overlap => new TaskCreateResult(TaskCreateError.Overlap, validation.Message),
                    _ => new TaskCreateResult(TaskCreateError.InvalidWindow, validation.Message)
                };
            }

            var task = new TaskEntity
            {
                PlanId = draft.Id,
                TechnicianId = technicianId,
                RegionId = validation.RegionId,
                Title = normalizedTitle,
                Location = normalizedLocation,
                StartAt = validation.StartAt,
                EndAt = validation.EndAt,
                Priority = priority,
                Status = status,
                CreatedAt = DateTime.UtcNow
            };

            _db.Tasks.Add(task);
            await _db.SaveChangesAsync(cancellationToken);

            await _evaluationEngine.RecalculatePlanAsync(draft.Id, cancellationToken);
            return new TaskCreateResult(TaskCreateError.None, Task: task);
        }

        public async Task<TaskEntity> UpdateTaskAsync(long taskId, Action<TaskEntity> applyChanges, string role, CancellationToken cancellationToken = default)
        {
            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                throw new InvalidOperationException($"Task {taskId} was not found.");
            }

            var canEdit = await _taskPermissionService.CanEditTaskAsync(task, role, cancellationToken);
            if (!canEdit)
            {
                throw new InvalidOperationException("Neturite teisiu redaguoti sios uzduoties.");
            }

            applyChanges(task);
            await _db.SaveChangesAsync(cancellationToken);
            if (task.PlanId.HasValue)
            {
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
            }
              return task;
          }

        public async Task<TaskMoveResult> MoveTaskAsync(long taskId, int targetTechnicianId, DateTime targetDate, int targetSlotStartMinutes, uint expectedVersion, string role, CancellationToken cancellationToken = default)
        {
            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                return new TaskMoveResult(TaskMoveError.NotFound, $"Task {taskId} was not found.");
            }

            var currentVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            if (expectedVersion == 0 || expectedVersion != currentVersion)
            {
                return new TaskMoveResult(TaskMoveError.Stale, "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.", CurrentVersion: currentVersion);
            }

            var duration = task.EndAt - task.StartAt;
            var durationMinutesRaw = duration.TotalMinutes;
            if (durationMinutesRaw <= 0 || Math.Abs(durationMinutesRaw - Math.Round(durationMinutesRaw)) > 0.0001)
            {
                return new TaskMoveResult(TaskMoveError.InvalidWindow, "Netinkama uzduoties trukme.");
            }

            var durationMinutes = (int)Math.Round(durationMinutesRaw);
            var endMinutes = targetSlotStartMinutes + durationMinutes;

            var validation = await ValidatePlacementAsync(
                ignoreTaskId: taskId,
                targetTechnicianId: targetTechnicianId,
                targetDate: targetDate,
                startMinutes: targetSlotStartMinutes,
                endMinutes: endMinutes,
                plan: task.Plan,
                status: task.Status,
                role: role,
                requireEndAligned: false,
                cancellationToken: cancellationToken);

            if (validation.Error != PlacementError.None)
            {
                return validation.Error switch
                {
                    PlacementError.NotEditable => new TaskMoveResult(TaskMoveError.NotEditable, "Neturite teisiu perkelti sios uzduoties."),
                    PlacementError.TechnicianNotFound => new TaskMoveResult(TaskMoveError.TechnicianNotFound, validation.Message),
                    PlacementError.Overlap => new TaskMoveResult(TaskMoveError.Overlap, validation.Message),
                    _ => new TaskMoveResult(TaskMoveError.InvalidWindow, validation.Message)
                };
            }

            task.TechnicianId = targetTechnicianId;
            task.RegionId = validation.RegionId;
            task.StartAt = validation.StartAt;
            task.EndAt = validation.EndAt;

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new TaskMoveResult(TaskMoveError.Stale, "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.", CurrentVersion: currentVersion);
            }

            if (task.PlanId.HasValue)
            {
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
            }

            return new TaskMoveResult(TaskMoveError.None, Task: task, CurrentVersion: _db.Entry(task).Property<uint>("xmin").CurrentValue);
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
