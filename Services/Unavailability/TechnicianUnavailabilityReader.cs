using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.Unavailability
{
    public sealed record TechnicianBlockedInterval(DateTime StartAtUtc, DateTime EndAtUtc);

    public sealed class TechnicianUnavailabilityReader
    {
        private readonly ScheduleDbContext _db;

        public TechnicianUnavailabilityReader(ScheduleDbContext db)
        {
            _db = db;
        }

        public async Task<IReadOnlyList<TechnicianBlockedInterval>> GetBlockedIntervalsAsync(
            long technicianId,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            CancellationToken cancellationToken)
        {
            if (technicianId <= 0 || rangeEndUtc <= rangeStartUtc)
            {
                return Array.Empty<TechnicianBlockedInterval>();
            }

            var blockedPeriods = await _db.TechnicianUnavailabilities
                .AsNoTracking()
                .Where(u => u.TechnicianId == technicianId)
                .Where(u => u.PeriodStart < rangeEndUtc && u.PeriodEnd > rangeStartUtc)
                .Select(u => new { u.PeriodStart, u.PeriodEnd })
                .ToListAsync(cancellationToken);

            return blockedPeriods
                .Select(period => new TechnicianBlockedInterval(period.PeriodStart, period.PeriodEnd))
                .Where(interval => HasHalfOpenOverlap(interval.StartAtUtc, interval.EndAtUtc, rangeStartUtc, rangeEndUtc))
                .OrderBy(interval => interval.StartAtUtc)
                .ToList();
        }

        public async Task<Dictionary<long, List<TechnicianBlockedInterval>>> GetBlockedIntervalsByTechnicianAsync(
            IReadOnlyCollection<long> technicianIds,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<long, List<TechnicianBlockedInterval>>();
            if (rangeEndUtc <= rangeStartUtc)
            {
                return result;
            }

            var normalizedTechnicianIds = technicianIds
                .Where(id => id > 0)
                .Distinct()
                .ToArray();

            if (normalizedTechnicianIds.Length == 0)
            {
                return result;
            }

            var blockedPeriods = await _db.TechnicianUnavailabilities
                .AsNoTracking()
                .Where(u => normalizedTechnicianIds.Contains(u.TechnicianId))
                .Where(u => u.PeriodStart < rangeEndUtc && u.PeriodEnd > rangeStartUtc)
                .Select(u => new { u.TechnicianId, u.PeriodStart, u.PeriodEnd })
                .ToListAsync(cancellationToken);

            foreach (var blockedPeriod in blockedPeriods)
            {
                if (!HasHalfOpenOverlap(blockedPeriod.PeriodStart, blockedPeriod.PeriodEnd, rangeStartUtc, rangeEndUtc))
                {
                    continue;
                }

                if (!result.TryGetValue(blockedPeriod.TechnicianId, out var intervals))
                {
                    intervals = new List<TechnicianBlockedInterval>();
                    result[blockedPeriod.TechnicianId] = intervals;
                }

                intervals.Add(new TechnicianBlockedInterval(blockedPeriod.PeriodStart, blockedPeriod.PeriodEnd));
            }

            foreach (var entry in result)
            {
                entry.Value.Sort(static (left, right) => left.StartAtUtc.CompareTo(right.StartAtUtc));
            }

            return result;
        }

        public bool HasBlockedOverlap(
            IEnumerable<TechnicianBlockedInterval> blockedIntervals,
            DateTime startAtUtc,
            DateTime endAtUtc)
        {
            return blockedIntervals.Any(interval => HasHalfOpenOverlap(interval.StartAtUtc, interval.EndAtUtc, startAtUtc, endAtUtc));
        }

        public bool TouchesOrOverlapsWindow(
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            DateTime blockedStartUtc,
            DateTime blockedEndUtc)
        {
            return TouchesOrOverlaps(blockedStartUtc, blockedEndUtc, rangeStartUtc, rangeEndUtc);
        }

        public bool TouchesOrOverlaps(
            TechnicianBlockedInterval blockedInterval,
            DateTime startAtUtc,
            DateTime endAtUtc)
        {
            return TouchesOrOverlaps(blockedInterval.StartAtUtc, blockedInterval.EndAtUtc, startAtUtc, endAtUtc);
        }

        private static bool HasHalfOpenOverlap(
            DateTime leftStartUtc,
            DateTime leftEndUtc,
            DateTime rightStartUtc,
            DateTime rightEndUtc)
        {
            return leftStartUtc < rightEndUtc && leftEndUtc > rightStartUtc;
        }

        private static bool TouchesOrOverlaps(
            DateTime leftStartUtc,
            DateTime leftEndUtc,
            DateTime rightStartUtc,
            DateTime rightEndUtc)
        {
            return leftStartUtc <= rightEndUtc && leftEndUtc >= rightStartUtc;
        }
    }
}
