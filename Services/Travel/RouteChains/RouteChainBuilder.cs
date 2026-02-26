using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.Travel.RouteChains
{
    public sealed class RouteChainBuilder : IRouteChainBuilder
    {
        private readonly ScheduleDbContext _db;

        public RouteChainBuilder(ScheduleDbContext db)
        {
            _db = db;
        }

        public async Task<RouteChain?> BuildDayChainAsync(
            RouteChainRequest request,
            CancellationToken cancellationToken = default)
        {
            var workplaceAddressId = await _db.Technicians
                .Where(t => t.Id == request.TechnicianId)
                .Select(t => t.WorkplaceAddressId)
                .FirstOrDefaultAsync(cancellationToken);
            if (workplaceAddressId <= 0)
            {
                return null;
            }

            var dayStart = request.DayUtc.Date;
            var dayEnd = dayStart.AddDays(1);
            var query = _db.Tasks
                .AsNoTracking()
                .Where(t => t.TechnicianId == request.TechnicianId)
                .Where(t => t.StartAt >= dayStart && t.StartAt < dayEnd);

            if (!request.IncludeCanceled)
            {
                query = query.Where(t => t.Status != ScheduleTaskStatus.Canceled);
            }

            if (request.ExcludeTaskId.HasValue)
            {
                var excludeId = request.ExcludeTaskId.Value;
                query = query.Where(t => t.Id != excludeId);
            }

            if (request.FilterByPlanId)
            {
                if (request.PlanId.HasValue)
                {
                    var planId = request.PlanId.Value;
                    query = query.Where(t => t.PlanId == planId);
                }
                else
                {
                    query = query.Where(t => t.PlanId == null);
                }
            }

            var stops = await query
                .OrderBy(t => t.StartAt)
                .Select(t => new RouteStop
                {
                    TaskId = t.Id,
                    StartAt = t.StartAt,
                    EndAt = t.EndAt,
                    AddressId = t.ServiceObject.AddressId,
                    IsProposed = false
                })
                .ToListAsync(cancellationToken);

            if (request.ProposedStop != null)
            {
                stops.Add(new RouteStop
                {
                    StartAt = request.ProposedStop.StartAt,
                    EndAt = request.ProposedStop.EndAt,
                    AddressId = request.ProposedStop.AddressId,
                    IsProposed = true
                });
            }

            stops = stops
                .OrderBy(s => s.StartAt)
                .ThenBy(s => s.IsProposed ? 1 : 0)
                .ToList();

            return new RouteChain
            {
                TechnicianId = request.TechnicianId,
                WorkplaceAddressId = workplaceAddressId,
                Stops = stops
            };
        }
    }
}
