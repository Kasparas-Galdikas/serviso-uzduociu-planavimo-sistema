using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Responses.Authentication;

namespace ServiceTaskPlanningSystem.Services.Authentication
{
    public class LoginService : ILoginService
    {
        private readonly ScheduleDbContext _db;
        private const int LoginLookupMaxAttempts = 8;

        public LoginService(ScheduleDbContext db)
        {
            _db = db;
        }

        public LoginResultDto ValidateLogin(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return LoginResultDto.Failure(LoginFailureCode.MissingEmail);
            }

            var trimmedEmail = email.Trim();
            var user = FindActiveUserByEmailWithRetry(trimmedEmail);

            if (user == null)
            {
                return LoginResultDto.Failure(LoginFailureCode.UserNotFoundOrInactive);
            }

            return LoginResultDto.SuccessResult(user);
        }

        public string GetRoleString(UserEntity user)
        {
            return user.Role == UserRole.Manager ? "manager" : "technician";
        }

        private UserEntity? FindActiveUserByEmailWithRetry(string email)
        {
            Exception? lastException = null;

            for (var attempt = 0; attempt < LoginLookupMaxAttempts; attempt++)
            {
                try
                {
                    return _db.Users.FirstOrDefault(u => u.Email == email && u.IsActive);
                }
                catch (Exception ex) when (IsTransientDatabaseStartupFailure(ex))
                {
                    lastException = ex;
                    Thread.Sleep(1000 * (attempt + 1));
                }
            }

            if (lastException != null)
            {
                throw lastException;
            }

            return null;
        }

        private static bool IsTransientDatabaseStartupFailure(Exception exception)
        {
            if (exception is InvalidOperationException invalidOperation &&
                invalidOperation.Message.Contains("transient failure", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (exception is NpgsqlException or SocketException)
            {
                return true;
            }

            if (exception is DbUpdateException dbUpdateException && dbUpdateException.InnerException != null)
            {
                return IsTransientDatabaseStartupFailure(dbUpdateException.InnerException);
            }

            return exception.InnerException != null && IsTransientDatabaseStartupFailure(exception.InnerException);
        }
    }
}
