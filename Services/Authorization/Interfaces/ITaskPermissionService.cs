using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Models;

namespace ServiceTaskPlanningSystem.Services.Authorization
{
    public interface ITaskPermissionService
    {
        Task<bool> CanEditTaskAsync(TaskEntity task, UserContext user, int timezoneOffsetMinutes);
    }
}
