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
        private readonly ILogger<PlanningCalendar> _logger;

        public PlanningCalendar(ScheduleDbContext db, ILogger<PlanningCalendar> logger)
        {
            _db = db;
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

            var today = DateTime.Today;
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

            if ((requestedStartDate.HasValue && requestedStartDate.Value < boundary.FirstFreeDate) ||
                (requestedEndDate.HasValue && requestedEndDate.Value < boundary.FirstFreeDate))
            {
                throw new InvalidOperationException($"Drafts may start from {boundary.FirstFreeDate:yyyy-MM-dd}.");
            }

            var requestedSpanDays = requestedStartDate.HasValue && requestedEndDate.HasValue && requestedEndDate.Value >= requestedStartDate.Value
                ? (requestedEndDate.Value - requestedStartDate.Value).Days + 1
                : 0;

            var startDate = requestedStartDate.HasValue && requestedStartDate.Value >= boundary.FirstFreeDate
                ? requestedStartDate.Value
                : boundary.FirstFreeDate;

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

            _logger.LogInformation("svc.plan.calendar.window start={Start} end={End} days={Days} weeks={Weeks} firstFree={FirstFree}", startDate, endDate, plannedSpanDays, weeks, boundary.FirstFreeDate);

            return new PlanningWindow(startDate, endDate, plannedSpanDays, weeks, boundary.FirstFreeDate);
        }
    }
}
