using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;

namespace ServiceTaskPlanningSystem.Services.Technicians
{
    public class TechnicianLookupService : ITechnicianLookupService
    {
        private readonly ScheduleDbContext _db;

        public TechnicianLookupService(ScheduleDbContext db)
        {
            _db = db;
        }

        public async Task<List<TechnicianLookupDto>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            var technicians = await _db.Technicians
                .AsNoTracking()
                .Include(t => t.User)
                .Include(t => t.WorkplaceAddress)
                .OrderBy(t => t.User != null ? t.User.Name : t.Id.ToString())
                .ToListAsync(cancellationToken);

            return technicians.Select(t => new TechnicianLookupDto
            {
                Id   = t.Id,
                Name = t.User != null
                    ? (!string.IsNullOrWhiteSpace(t.User.Name) ? t.User.Name : t.User.Email)
                    : $"Technician #{t.Id}",
                City = t.WorkplaceAddress?.City
            }).ToList();
        }
    }
}
