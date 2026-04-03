using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.Unavailability
{
    public class UnavailabilityService : IUnavailabilityService
    {
        private readonly ScheduleDbContext _db;
        private readonly ILogger<UnavailabilityService> _logger;

        public UnavailabilityService(
            ScheduleDbContext db,
            ILogger<UnavailabilityService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<List<TechnicianUnavailabilityEntity>> GetPeriodsAsync(
            long technicianId,
            CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("svc.unavailability.get-periods technicianId={TechnicianId}", technicianId);

            return await _db.TechnicianUnavailabilities
                .AsNoTracking()
                .Where(u => u.TechnicianId == technicianId)
                .OrderBy(u => u.PeriodStart)
                .ToListAsync(cancellationToken);
        }
    }
}
