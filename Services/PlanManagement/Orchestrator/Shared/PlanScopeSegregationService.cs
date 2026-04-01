using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared
{
    internal sealed class PlanScopeSegregationService
    {
        private readonly ScheduleDbContext _db;
        private readonly IScheduleTimeService _timeService;
        private readonly ILogger<PlanScopeSegregationService> _logger;

        internal sealed record PlanScopeSegregationResult(
            bool WorkPerformed,
            long? ActivePlanId,
            int MovedTaskCount,
            int CreatedSupersededPlanCount,
            int ReusedSupersededPlanCount,
            IReadOnlyList<long> AffectedSupersededPlanIds);

        public PlanScopeSegregationService(
            ScheduleDbContext db,
            IScheduleTimeService timeService,
            ILogger<PlanScopeSegregationService> logger)
        {
            _db = db;
            _timeService = timeService;
            _logger = logger;
        }

        internal async Task<PlanScopeSegregationResult> EnsureActivePlanScopedAsync(CancellationToken cancellationToken = default)
        {
            var activePlan = await _db.Plans
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);

            if (activePlan == null)
            {
                return new PlanScopeSegregationResult(false, null, 0, 0, 0, Array.Empty<long>());
            }

            var activeWeek = _timeService.GetCurrentWeekBoundary();
            var activeWeekStart = activeWeek.LocalStart;
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            var validPlanIds = await _db.Plans
                .AsNoTracking()
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);

            var orphanedTasks = await _db.Tasks
                .Where(t => t.PlanId.HasValue && !validPlanIds.Contains(t.PlanId.Value))
                .OrderBy(t => t.StartAt)
                .ToListAsync(cancellationToken);

            var detachedTasks = await _db.Tasks
                .Where(t => !t.PlanId.HasValue)
                .OrderBy(t => t.StartAt)
                .ToListAsync(cancellationToken);

            var oldActiveTasks = await _db.Tasks
                .Where(t => t.PlanId == activePlan.Id && t.StartAt.Date < activeWeekStart)
                .OrderBy(t => t.StartAt)
                .ToListAsync(cancellationToken);

            var createdPlanCount = 0;
            var reusedPlanCount = 0;
            var affectedSupersededPlanIds = new HashSet<long>();
            var workPerformed = false;
            var movedTaskCount = 0;

            var orphanedHistoricalTasks = orphanedTasks
                .Where(t => t.StartAt.Date < activeWeekStart)
                .ToList();
            var orphanedActiveScopeTasks = orphanedTasks
                .Where(t => t.StartAt.Date >= activeWeekStart)
                .ToList();
            var detachedHistoricalTasks = detachedTasks
                .Where(t => t.StartAt.Date < activeWeekStart)
                .ToList();
            var detachedActiveScopeTasks = detachedTasks
                .Where(t => t.StartAt.Date >= activeWeekStart)
                .ToList();

            foreach (var task in orphanedActiveScopeTasks)
            {
                task.PlanId = activePlan.Id;
            }

            foreach (var task in detachedActiveScopeTasks)
            {
                task.PlanId = activePlan.Id;
            }

            if (orphanedActiveScopeTasks.Count > 0)
            {
                movedTaskCount += orphanedActiveScopeTasks.Count;
                workPerformed = true;
                _logger.LogInformation(
                    "svc.plan.scope-segregation.orphans.reassigned-to-active activePlanId={ActivePlanId} taskCount={TaskCount}",
                    activePlan.Id,
                    orphanedActiveScopeTasks.Count);
            }

            if (detachedActiveScopeTasks.Count > 0)
            {
                movedTaskCount += detachedActiveScopeTasks.Count;
                workPerformed = true;
                _logger.LogInformation(
                    "svc.plan.scope-segregation.detached.reassigned-to-active activePlanId={ActivePlanId} taskCount={TaskCount}",
                    activePlan.Id,
                    detachedActiveScopeTasks.Count);
            }

            var candidateTasks = oldActiveTasks
                .Concat(orphanedHistoricalTasks)
                .Concat(detachedHistoricalTasks)
                .GroupBy(t => t.Id)
                .Select(g => g.First())
                .OrderBy(t => t.StartAt)
                .ToList();

            if (candidateTasks.Count == 0)
            {
                var normalized = await NormalizeActivePlanStartAsync(activePlan, activeWeekStart, cancellationToken, saveImmediately: false);
                if (normalized)
                {
                    workPerformed = true;
                }
                if (workPerformed)
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                return new PlanScopeSegregationResult(
                    workPerformed,
                    activePlan.Id,
                    movedTaskCount,
                    0,
                    0,
                    Array.Empty<long>());
            }

            var supersededPlan = await _db.Plans
                .Where(p => p.Status == PlanStatus.Superseded)
                .Where(p => p.SupersededByPlanId == activePlan.Id)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (supersededPlan == null)
            {
                supersededPlan = new PlanEntity
                {
                    PeriodStart = candidateTasks.Min(t => t.StartAt.Date),
                    PeriodEnd = candidateTasks.Max(t => t.EndAt.Date),
                    Weeks = 1,
                    CreatedBy = activePlan.CreatedBy,
                    Status = PlanStatus.Superseded,
                    SupersededByPlanId = activePlan.Id,
                    CreatedAt = _timeService.GetCurrentUtcTime()
                };

                UpdatePlanSpan(supersededPlan, supersededPlan.PeriodStart, supersededPlan.PeriodEnd);
                _db.Plans.Add(supersededPlan);
                createdPlanCount++;
            }
            else
            {
                reusedPlanCount++;
            }

            if (createdPlanCount > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }

            foreach (var task in candidateTasks)
            {
                task.PlanId = supersededPlan.Id;
            }

            affectedSupersededPlanIds.Add(supersededPlan.Id);
            movedTaskCount += candidateTasks.Count;
            workPerformed = true;
            var movedStart = candidateTasks.Min(t => t.StartAt.Date);
            var movedEnd = candidateTasks.Max(t => t.EndAt.Date);
            var supersededStart = supersededPlan.PeriodStart.Date <= movedStart ? supersededPlan.PeriodStart.Date : movedStart;
            var supersededEnd = supersededPlan.PeriodEnd.Date >= movedEnd ? supersededPlan.PeriodEnd.Date : movedEnd;
            UpdatePlanSpan(supersededPlan, supersededStart, supersededEnd);
            _db.Plans.Update(supersededPlan);

            var activePlanNormalized = await NormalizeActivePlanStartAsync(activePlan, activeWeekStart, cancellationToken, saveImmediately: false);
            if (activePlanNormalized)
            {
                workPerformed = true;
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "svc.plan.scope-segregation activePlanId={ActivePlanId} movedTasks={MovedTaskCount} orphanedTasks={OrphanedTaskCount} detachedTasks={DetachedTaskCount} createdSupersededPlans={CreatedPlanCount} reusedSupersededPlans={ReusedPlanCount} activeWeekStart={ActiveWeekStart:yyyy-MM-dd}",
                activePlan.Id,
                movedTaskCount,
                orphanedTasks.Count,
                detachedTasks.Count,
                createdPlanCount,
                reusedPlanCount,
                activeWeekStart);

            return new PlanScopeSegregationResult(
                workPerformed,
                activePlan.Id,
                movedTaskCount,
                createdPlanCount,
                reusedPlanCount,
                affectedSupersededPlanIds.OrderBy(id => id).ToArray());
        }

        private async Task<bool> NormalizeActivePlanStartAsync(
            PlanEntity activePlan,
            DateTime activeWeekStart,
            CancellationToken cancellationToken,
            bool saveImmediately = true)
        {
            if (activePlan.PeriodStart.Date >= activeWeekStart)
            {
                return false;
            }

            UpdatePlanSpan(activePlan, activeWeekStart, activePlan.PeriodEnd.Date < activeWeekStart ? activeWeekStart : activePlan.PeriodEnd.Date);
            _db.Plans.Update(activePlan);

            if (saveImmediately)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation(
                "svc.plan.scope-segregation.active-normalized planId={PlanId} periodStart={PeriodStart:yyyy-MM-dd} periodEnd={PeriodEnd:yyyy-MM-dd}",
                activePlan.Id,
                activePlan.PeriodStart,
                activePlan.PeriodEnd);

            return true;
        }

        private static void UpdatePlanSpan(PlanEntity plan, DateTime periodStart, DateTime periodEnd)
        {
            plan.PeriodStart = periodStart.Date;
            plan.PeriodEnd = periodEnd.Date < plan.PeriodStart ? plan.PeriodStart : periodEnd.Date;
            var totalDays = (plan.PeriodEnd.Date - plan.PeriodStart.Date).Days + 1;
            plan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));
        }
    }
}
