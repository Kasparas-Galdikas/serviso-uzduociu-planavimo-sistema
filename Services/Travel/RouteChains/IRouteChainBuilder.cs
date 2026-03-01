using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.Travel.RouteChains
{
    public interface IRouteChainBuilder
    {
        Task<RouteChain?> BuildDayChainAsync(
            RouteChainRequest request,
            CancellationToken cancellationToken = default);
    }
}
