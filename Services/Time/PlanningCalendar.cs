using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.Time
{
    public class PlanningCalendar : IPlanningCalendar
    {
        private readonly ScheduleDbContext _db;
        private readonly IScheduleTimeService _timeService;
        private readonly ILogger<PlanningCalendar> _logger;

        public PlanningCalendar(ScheduleDbContext db, IScheduleTimeService timeService, ILogger<PlanningCalendar> logger)
        {
            _db = db;
            _timeService = timeService;
            _logger = logger;
        }

        public async Task<PlanningBoundary> GetBoundaryAsync(CancellationToken cancellationToken = default)
        {
            // Only consider Active and Superseded plans (exclude Draft from boundary calculation)
            var lastPlanEnd = await _db.Plans
                .Where(p => p.Status == PlanStatus.Active || p.Status == PlanStatus.Superseded)
                .MaxAsync(p => (DateTime?)p.PeriodEnd, cancellationToken);
            var lastTaskEnd = await _db.Tasks.MaxAsync(t => (DateTime?)t.EndAt, cancellationToken);

            var lastOccupied = new List<DateTime?> { lastPlanEnd, lastTaskEnd }
                .Where(d => d.HasValue)
                .Select(d => (DateTime?)d!.Value.Date)
                .DefaultIfEmpty((DateTime?)null)
                .Max();

            var today = _timeService.GetLocalToday();
            var firstFreeDate = lastOccupied == null || lastOccupied == default
                ? today
                : lastOccupied.Value.AddDays(1);

            if (firstFreeDate < today)
            {
                firstFreeDate = today;
            }

            return new PlanningBoundary(firstFreeDate, lastOccupied);
        }

        public async Task<PlanningWindow> BuildWindowAsync(DateTime? requestedStart, DateTime? requestedEnd, int? requestedWeeks, int fallbackWeeks, CancellationToken cancellationToken = default)
        {
            var boundary = await GetBoundaryAsync(cancellationToken);
            var requestedStartDate = requestedStart?.Date;
            var requestedEndDate = requestedEnd?.Date;
            var today = _timeService.GetLocalToday();

            if ((requestedStartDate.HasValue && requestedStartDate.Value < today) ||
                (requestedEndDate.HasValue && requestedEndDate.Value < today))
            {
                throw new InvalidOperationException($"Drafts may start from {today:yyyy-MM-dd}.");
            }

            var requestedSpanDays = requestedStartDate.HasValue && requestedEndDate.HasValue && requestedEndDate.Value >= requestedStartDate.Value
                ? (requestedEndDate.Value - requestedStartDate.Value).Days + 1
                : 0;

            var startDate = requestedStartDate.HasValue && requestedStartDate.Value >= today
                ? requestedStartDate.Value
                : today;

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

            _logger.LogInformation("svc.plan.calendar.window start={Start} end={End} days={Days} weeks={Weeks} firstFree={FirstFree} today={Today}", startDate, endDate, plannedSpanDays, weeks, boundary.FirstFreeDate, today);

            return new PlanningWindow(startDate, endDate, plannedSpanDays, weeks, boundary.FirstFreeDate);
        }
    }
}
