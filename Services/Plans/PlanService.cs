using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.Plans
{
    public class PlanService : IPlanService
    {
        private readonly ScheduleDbContext _db;

        public PlanService(ScheduleDbContext db)
        {
            _db = db;
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

            var startDate = requestedStart?.Date ?? activePlan.PeriodEnd.Date.AddDays(1);
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
            return task;
        }

        public async Task PublishDraftAsync(CancellationToken cancellationToken = default)
        {
            var draft = await _db.Plans
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);

            if (draft == null)
            {
                throw new InvalidOperationException("No draft plan to publish.");
            }

            var active = await _db.Plans
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            // Validation hook: place draft validation here before flipping states.

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
