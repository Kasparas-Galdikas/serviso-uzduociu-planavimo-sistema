using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/technicians")]
    [AllowAnonymous]
    public class TechniciansController : ControllerBase
    {
        private readonly ScheduleDbContext _db;

        public TechniciansController(ScheduleDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Get list of all active technicians for assignment dropdown
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetTechnicians(CancellationToken cancellationToken)
        {
            var technicians = await _db.Technicians
                .AsNoTracking()
                .Include(t => t.User)
                .Include(t => t.Region)
                .OrderBy(t => t.User != null ? t.User.Name : t.Id.ToString())
                .Select(t => new
                {
                    id = t.Id,
                    name = t.User != null
                        ? (!string.IsNullOrWhiteSpace(t.User.Name) ? t.User.Name : t.User.Email)
                        : $"Technician #{t.Id}",
                    region = t.Region != null ? t.Region.Name : null
                })
                .ToListAsync(cancellationToken);

            return Ok(technicians);
        }
    }
}
