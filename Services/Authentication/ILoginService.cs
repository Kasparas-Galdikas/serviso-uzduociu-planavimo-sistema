using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Responses.Authentication;

namespace ServiceTaskPlanningSystem.Services.Authentication
{
    public interface ILoginService
    {
        /// <summary>
        /// Validates user login by email.
        /// </summary>
        /// <param name="email">User's email address</param>
        /// <returns>LoginResultDto with success status, user data, or failure code</returns>
        LoginResultDto ValidateLogin(string email);

        /// <summary>
        /// Gets the role string for authentication claims.
        /// </summary>
        /// <param name="user">The user entity</param>
        /// <returns>"manager" or "technician"</returns>
        string GetRoleString(UserEntity user);
    }
}
