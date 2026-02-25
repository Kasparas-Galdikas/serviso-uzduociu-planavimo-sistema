using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Scheduling;

namespace ServiceTaskPlanningSystem.Services.Travel
{
    public sealed class TaskTravelFeasibilityService : ITaskTravelFeasibilityService
    {
        private sealed record DaySlot(DateTime StartAt, DateTime EndAt, long AddressId, bool IsProposed);

        private readonly ScheduleDbContext _db;

        public TaskTravelFeasibilityService(ScheduleDbContext db)
        {
            _db = db;
        }

        public async Task<TravelFeasibilityResult> EvaluatePlacementAsync(
            long technicianId,
            DateTime proposedStartAt,
            DateTime proposedEndAt,
            long serviceAddressId,
            int proposedStartMinutesLocal,
            long? excludeTaskId,
            bool checkOverlap,
            CancellationToken cancellationToken = default)
        {
            var workplaceAddressId = await _db.Technicians
                .Where(t => t.Id == technicianId)
                .Select(t => t.WorkplaceAddressId)
                .FirstOrDefaultAsync(cancellationToken);
            if (workplaceAddressId <= 0)
            {
                return new TravelFeasibilityResult { IsFeasible = false, Message = "Techniko darbo vietos adresas nerastas." };
            }

            var dayStart = proposedStartAt.Date;
            var dayEnd = dayStart.AddDays(1);
            var daySlots = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.TechnicianId == technicianId)
                .Where(t => t.StartAt >= dayStart && t.StartAt < dayEnd)
                .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                .Where(t => excludeTaskId == null || t.Id != excludeTaskId.Value)
                .OrderBy(t => t.StartAt)
                .Select(t => new DaySlot(t.StartAt, t.EndAt, t.ServiceObject.AddressId, false))
                .ToListAsync(cancellationToken);

            if (checkOverlap && daySlots.Any(t => t.StartAt < proposedEndAt && t.EndAt > proposedStartAt))
            {
                return new TravelFeasibilityResult
                {
                    IsFeasible = false,
                    HasOverlap = true,
                    Message = "Laiko langas uzimtas (persidengia uzduotys)."
                };
            }

            var sequence = daySlots
                .Append(new DaySlot(proposedStartAt, proposedEndAt, serviceAddressId, true))
                .OrderBy(t => t.StartAt)
                .ThenBy(t => t.IsProposed ? 1 : 0)
                .ToList();
            var idx = sequence.FindIndex(t => t.IsProposed);
            if (idx < 0)
            {
                return new TravelFeasibilityResult { IsFeasible = false, Message = "Nepavyko ivertinti keliones." };
            }

            var pairsToCheck = new List<(long From, long To, int Available, bool IsInbound, bool IsOutbound)>();
            if (idx == 0)
            {
                var available = proposedStartMinutesLocal - ScheduleGrid.DayStartMinutes;
                pairsToCheck.Add((workplaceAddressId, serviceAddressId, available, true, false));
            }
            else
            {
                var prev = sequence[idx - 1];
                var available = (int)(proposedStartAt - prev.EndAt).TotalMinutes;
                pairsToCheck.Add((prev.AddressId, serviceAddressId, available, true, false));
            }

            if (idx < sequence.Count - 1)
            {
                var next = sequence[idx + 1];
                var available = (int)(next.StartAt - proposedEndAt).TotalMinutes;
                pairsToCheck.Add((serviceAddressId, next.AddressId, available, false, true));
            }

            var fromIds = pairsToCheck.Select(p => p.From).Distinct().ToList();
            var toIds = pairsToCheck.Select(p => p.To).Distinct().ToList();
            var routes = await _db.TravelRoutes
                .AsNoTracking()
                .Where(r => fromIds.Contains(r.FromAddressId) && toIds.Contains(r.ToAddressId))
                .ToDictionaryAsync(r => (r.FromAddressId, r.ToAddressId), r => r.DurationMinutes, cancellationToken);

            int? inbound = null;
            int? outbound = null;
            foreach (var (from, to, available, isInbound, isOutbound) in pairsToCheck)
            {
                if (!routes.TryGetValue((from, to), out var required))
                {
                    continue;
                }

                if (isInbound) inbound = required;
                if (isOutbound) outbound = required;

                if (required > 0 && required > available)
                {
                    var shownAvailable = Math.Max(0, available);
                    return new TravelFeasibilityResult
                    {
                        IsFeasible = false,
                        HasOverlap = false,
                        InboundTravelMinutes = inbound,
                        OutboundTravelMinutes = outbound,
                        Message = $"Nepakanka laiko kelionei: reikia {required} min., galima {shownAvailable} min."
                    };
                }
            }

            return new TravelFeasibilityResult
            {
                IsFeasible = true,
                HasOverlap = false,
                InboundTravelMinutes = inbound,
                OutboundTravelMinutes = outbound
            };
        }
    }
}
