using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Models.DTOs.Responses.Authentication
{
    public class LoginResultDto
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public UserEntity? User { get; set; }

        public static LoginResultDto Failure(string errorMessage)
        {
            return new LoginResultDto
            {
                Success = false,
                ErrorMessage = errorMessage,
                User = null
            };
        }

        public static LoginResultDto SuccessResult(UserEntity user)
        {
            return new LoginResultDto
            {
                Success = true,
                ErrorMessage = null,
                User = user
            };
        }
    }
}
