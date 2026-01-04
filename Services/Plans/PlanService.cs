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

        public async Task<PlanEntity> CreateDraftFromActiveAsync(CancellationToken cancellationToken = default)
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

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            var draftPlan = new PlanEntity
            {
                PeriodStart = activePlan.PeriodStart,
                PeriodEnd = activePlan.PeriodEnd,
                Weeks = activePlan.Weeks,
                Status = PlanStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = activePlan.CreatedBy
            };

            _db.Plans.Add(draftPlan);
            await _db.SaveChangesAsync(cancellationToken);

            var clonedTasks = activePlan.Tasks
                .Select(task => new TaskEntity
                {
                    PlanId = draftPlan.Id,
                    TechnicianId = task.TechnicianId,
                    Title = task.Title,
                    Location = task.Location,
                    RegionId = task.RegionId,
                    StartAt = task.StartAt,
                    EndAt = task.EndAt,
                    Priority = task.Priority,
                    Status = task.Status,
                    CreatedAt = DateTime.UtcNow
                })
                .ToList();

            _db.Tasks.AddRange(clonedTasks);
            await _db.SaveChangesAsync(cancellationToken);

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

            if (draftExists && task.Plan?.Status != PlanStatus.Draft)
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
    }
}
