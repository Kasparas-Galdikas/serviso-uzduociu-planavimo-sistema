using ServiceTaskPlanningSystem.Services.Authorization.Models;

namespace ServiceTaskPlanningSystem.Services.Authorization
{
    public interface IUserContextResolver
    {
        UserContext GetCurrentUser();
    }
}
