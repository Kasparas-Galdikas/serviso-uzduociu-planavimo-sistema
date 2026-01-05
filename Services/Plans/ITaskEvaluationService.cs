using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.Plans
{
    public interface ITaskEvaluationService
    {
        Task RecalculatePlanAsync(long planId, CancellationToken cancellationToken = default);
        Task RecalculateTasksAsync(IEnumerable<long> taskIds, CancellationToken cancellationToken = default);
    }
}
