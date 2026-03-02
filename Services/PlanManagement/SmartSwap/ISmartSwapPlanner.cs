using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.SmartSwap
{
    public interface ISmartSwapPlanner
    {
        Task<SmartSwapPlan> BuildPlanAsync(
            long sourceTaskId,
            long targetTaskId,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default);
    }
}

