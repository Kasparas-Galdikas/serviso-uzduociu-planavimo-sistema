using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Models;

namespace ServiceTaskPlanningSystem.Services.Authorization
{
    public class HttpUserContextResolver : IUserContextResolver
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ScheduleDbContext _db;

        public HttpUserContextResolver(IHttpContextAccessor httpContextAccessor, ScheduleDbContext db)
        {
            _httpContextAccessor = httpContextAccessor;
            _db = db;
        }

        public UserContext GetCurrentUser()
        {
            var principal = _httpContextAccessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return new UserContext { Role = UserRole.Technician };
            }

            var email = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(email))
            {
                return new UserContext { Role = UserRole.Technician };
            }

            // Look up user from database by email
            var user = _db.Users
                .AsNoTracking()
                .FirstOrDefault(u => u.Email == email && u.IsActive);

            if (user == null)
            {
                // User not found in database - return default technician context
                return new UserContext { Role = UserRole.Technician };
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
    }
}
