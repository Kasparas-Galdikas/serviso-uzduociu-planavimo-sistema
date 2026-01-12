using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Services.Authorization;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly IUserContextResolver _userContextResolver;

        public AuthController(IUserContextResolver userContextResolver)
        {
            _userContextResolver = userContextResolver;
        }

        [HttpGet("me")]
        [Authorize]
        public IActionResult Me()
        {
            var user = _userContextResolver.GetCurrentUser();
            return Ok(new
            {
                role = user.Role.ToString().ToLowerInvariant(),
                userId = user.UserId,
                technicianId = user.TechnicianId
            });
        }
    }
}
