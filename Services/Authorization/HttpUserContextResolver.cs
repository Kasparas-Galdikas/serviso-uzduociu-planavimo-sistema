using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Models;

namespace ServiceTaskPlanningSystem.Services.Authorization
{
    public class HttpUserContextResolver : IUserContextResolver
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ScheduleDbContext _db;
        private readonly ILogger<HttpUserContextResolver> _logger;

        public HttpUserContextResolver(IHttpContextAccessor httpContextAccessor, ScheduleDbContext db, ILogger<HttpUserContextResolver> logger)
        {
            _httpContextAccessor = httpContextAccessor;
            _db = db;
            _logger = logger;
        }

        public UserContext GetCurrentUser()
        {
            var principal = _httpContextAccessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return CreateAnonymous("not authenticated");
            }

            var email = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(email))
            {
                return CreateAnonymous("missing email claim");
            }

            // Look up user from database by email
            var user = _db.Users
                .AsNoTracking()
                .FirstOrDefault(u => u.Email == email && u.IsActive);

            if (user == null)
            {
                // User not found in database - return anonymous context
                return CreateAnonymous("user not found or inactive", email);
            }

            var technicianId = _db.Technicians
                .AsNoTracking()
                .Where(t => t.UserId == user.Id)
                .Select(t => (long?)t.Id)
                .FirstOrDefault();

            return new UserContext
            {
                UserId = user.Id,
                TechnicianId = technicianId,
                Role = user.Role
            };
        }

        private UserContext CreateAnonymous(string reason, string? email = null)
        {
            _logger.LogWarning("Anonymous user context resolved. Reason={Reason} Email={Email}", reason, email ?? "n/a");
            return new UserContext { Role = UserRole.Anonymous };
        }
    }
}
