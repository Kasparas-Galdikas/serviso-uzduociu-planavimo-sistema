using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Services.Technicians;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/technicians")]
    [AllowAnonymous]
    public class TechniciansController : ControllerBase
    {
        private readonly ITechnicianLookupService _technicianLookupService;

        public TechniciansController(ITechnicianLookupService technicianLookupService)
        {
            _technicianLookupService = technicianLookupService;
        }

        /// <summary>
        /// Get list of all technicians for assignment dropdown.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetTechnicians(CancellationToken cancellationToken)
        {
            var technicians = await _technicianLookupService.GetAllAsync(cancellationToken);
            return Ok(technicians);
        }
    }
}
