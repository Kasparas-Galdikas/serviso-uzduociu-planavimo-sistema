using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.Evaluation
{
    public interface IEvaluationEngine
    {
        Task RecalculateTasksAsync(IEnumerable<long> taskIds, CancellationToken cancellationToken = default);
        Task RecalculatePlanAsync(long planId, CancellationToken cancellationToken = default);
    }
}
