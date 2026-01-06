using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.Authorization
{
    public interface ITaskPermissionService
    {
        Task<bool> CanEditTaskAsync(TaskEntity task, string role, CancellationToken cancellationToken = default);
    }
}
