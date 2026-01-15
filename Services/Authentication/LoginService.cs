using System.Linq;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Responses.Authentication;

namespace ServiceTaskPlanningSystem.Services.Authentication
{
    public class LoginService : ILoginService
    {
        private readonly ScheduleDbContext _db;

        public LoginService(ScheduleDbContext db)
        {
            _db = db;
        }

        public LoginResultDto ValidateLogin(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return LoginResultDto.Failure("Prašome įvesti elektroninio pašto adresą.");
            }

            var trimmedEmail = email.Trim();
            var user = _db.Users.FirstOrDefault(u => u.Email == trimmedEmail && u.IsActive);

            if (user == null)
            {
                return LoginResultDto.Failure("Vartotojas su tokiu el. pašto adresu nerastas arba neaktyvus.");
            }

            return LoginResultDto.SuccessResult(user);
        }

        public string GetRoleString(UserEntity user)
        {
            return user.Role == UserRole.Manager ? "manager" : "technician";
        }
    }
}
