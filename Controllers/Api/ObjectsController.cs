using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/objects")]
    public class ObjectsController : ControllerBase
    {
        private readonly ScheduleDbContext _db;

        public ObjectsController(ScheduleDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<IActionResult> GetObjects(CancellationToken cancellationToken)
        {
            var objects = await _db.Objects
                .AsNoTracking()
                .OrderBy(o => o.Name)
                .Select(o => new ObjectLookupResponseDto
                {
                    Id = o.Id,
                    Name = o.Name,
                    Address = o.Address
                })
                .ToListAsync(cancellationToken);

            return Ok(objects);
        }
    }
}
