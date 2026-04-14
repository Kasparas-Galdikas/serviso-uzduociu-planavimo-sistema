using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Models.DTOs.Responses.Authentication
{
    public enum LoginFailureCode
    {
        None = 0,
        MissingEmail = 1,
        UserNotFoundOrInactive = 2,
    }

    public class LoginResultDto
    {
        public bool Success { get; set; }
        public LoginFailureCode FailureCode { get; set; }
        public UserEntity? User { get; set; }

        public static LoginResultDto Failure(LoginFailureCode failureCode)
        {
            return new LoginResultDto
            {
                Success = false,
                FailureCode = failureCode,
                User = null
            };
        }

        public static LoginResultDto SuccessResult(UserEntity user)
        {
            return new LoginResultDto
            {
                Success = true,
                FailureCode = LoginFailureCode.None,
                User = user
            };
        }
    }
}
