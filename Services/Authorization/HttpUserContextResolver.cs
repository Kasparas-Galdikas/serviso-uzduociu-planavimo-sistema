using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Models;

namespace ServiceTaskPlanningSystem.Services.Authorization
{
    public class HttpUserContextResolver : IUserContextResolver
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public HttpUserContextResolver(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public UserContext GetCurrentUser()
        {
            var principal = _httpContextAccessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return new UserContext { Role = UserRole.Technician };
            }

            var roleClaim = principal.FindFirst(ClaimTypes.Role)?.Value;
            var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            var role = roleClaim?.ToLowerInvariant() switch
            {
                "technician" => UserRole.Technician,
                _ => UserRole.Manager
            };

            long? userId = null;
            if (long.TryParse(userIdClaim, out var parsed))
            {
                userId = parsed;
            }

            return new UserContext
            {
                UserId = userId,
                Role = role
            };
        }
    }
}
