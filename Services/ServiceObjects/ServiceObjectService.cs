using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;

namespace ServiceTaskPlanningSystem.Services.ServiceObjects
{
    public class ServiceObjectService : IServiceObjectService
    {
        private readonly ScheduleDbContext _db;

        public ServiceObjectService(ScheduleDbContext db)
        {
            _db = db;
        }

        public Task<List<ObjectLookupResponseDto>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return _db.ServiceObjects
                .AsNoTracking()
                .OrderBy(o => o.Name)
                .Select(o => new ObjectLookupResponseDto
                {
                    Id = o.Id,
                    Name = o.Name,
                    Address = o.Address.FullAddress
                })
                .ToListAsync(cancellationToken);
        }
    }
}
