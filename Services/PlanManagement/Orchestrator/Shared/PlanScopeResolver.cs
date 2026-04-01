using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared
{
    internal sealed class PlanScopeResolver
    {
        private readonly ScheduleDbContext _db;
        private readonly IScheduleTimeService _timeService;

        internal sealed record PlanEvaluationScope(
            long PlanId,
            PlanStatus PlanStatus,
            DateTime? RangeStartUtc,
            DateTime? RangeEndUtcExclusive)
        {
            public bool HasDateWindow => RangeStartUtc.HasValue && RangeEndUtcExclusive.HasValue;
        }

        internal sealed record PlanConstraintScope(
            IReadOnlyList<long> PlanIds,
            bool IncludeUnplanned)
        {
            public bool HasPlanIds => PlanIds.Count > 0;
        }

        public PlanScopeResolver(ScheduleDbContext db, IScheduleTimeService timeService)
        {
            _db = db;
            _timeService = timeService;
        }

        internal async Task<PlanEvaluationScope?> ResolveEvaluationScopeAsync(
            long planId,
            CancellationToken cancellationToken = default)
        {
            var plan = await _db.Plans
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

            if (plan == null)
            {
                return null;
            }

            if (plan.Status == PlanStatus.Active)
            {
                var activeWeek = _timeService.GetCurrentWeekBoundary();
                return new PlanEvaluationScope(
                    plan.Id,
                    plan.Status,
                    activeWeek.UtcStart,
                    activeWeek.UtcEndExclusive);
            }

            return new PlanEvaluationScope(plan.Id, plan.Status, null, null);
        }

        internal IQueryable<TaskEntity> ApplyEvaluationScope(
            IQueryable<TaskEntity> query,
            PlanEvaluationScope scope)
        {
            var scoped = query.Where(t => t.PlanId == scope.PlanId);

            if (!scope.HasDateWindow)
            {
                return scoped;
            }

            var rangeStartUtc = scope.RangeStartUtc!.Value;
            var rangeEndUtcExclusive = scope.RangeEndUtcExclusive!.Value;
            return scoped.Where(t => t.StartAt >= rangeStartUtc && t.StartAt < rangeEndUtcExclusive);
        }

        internal async Task<PlanConstraintScope> ResolveConstraintScopeAsync(
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            CancellationToken cancellationToken = default)
        {
            if (rangeEndUtc <= rangeStartUtc)
            {
                return new PlanConstraintScope(Array.Empty<long>(), IncludeUnplanned: true);
            }

            var rangeStartDate = rangeStartUtc.Date;
            var inclusiveEndDate = rangeEndUtc.AddTicks(-1).Date;

            var planIds = await _db.Plans
                .AsNoTracking()
                .Where(p => p.PeriodStart <= inclusiveEndDate && p.PeriodEnd >= rangeStartDate)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);

            return new PlanConstraintScope(planIds, IncludeUnplanned: true);
        }

        internal IQueryable<TaskEntity> ApplyConstraintScope(
            IQueryable<TaskEntity> query,
            PlanConstraintScope scope)
        {
            if (!scope.HasPlanIds)
            {
                return scope.IncludeUnplanned
                    ? query.Where(t => !t.PlanId.HasValue)
                    : query.Where(_ => false);
            }

            return scope.IncludeUnplanned
                ? query.Where(t => !t.PlanId.HasValue || (t.PlanId.HasValue && scope.PlanIds.Contains(t.PlanId.Value)))
                : query.Where(t => t.PlanId.HasValue && scope.PlanIds.Contains(t.PlanId.Value));
        }
    }
}
