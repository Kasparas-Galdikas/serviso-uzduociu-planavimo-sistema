using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Services.ServiceObjects;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/objects")]
    public class ObjectsController : ControllerBase
    {
        private readonly IServiceObjectService _serviceObjectService;

        public ObjectsController(IServiceObjectService serviceObjectService)
        {
            _serviceObjectService = serviceObjectService;
        }

        [HttpGet]
        public async Task<IActionResult> GetObjects(CancellationToken cancellationToken)
        {
            var objects = await _serviceObjectService.GetAllAsync(cancellationToken);
            return Ok(objects);
        }
    }
}
