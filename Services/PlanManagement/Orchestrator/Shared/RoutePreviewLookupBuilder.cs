using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.Travel;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared
{
    internal sealed class RoutePreviewLookupBuilder
    {
        private readonly IRouteEvaluationService _routeEvaluationService;
        private readonly ITravelTimeResolver _travelResolver;
        private readonly ILogger<RoutePreviewLookupBuilder> _logger;

        public RoutePreviewLookupBuilder(
            IRouteEvaluationService routeEvaluationService,
            ITravelTimeResolver travelResolver,
            ILogger<RoutePreviewLookupBuilder> logger)
        {
            _routeEvaluationService = routeEvaluationService;
            _travelResolver = travelResolver;
            _logger = logger;
        }

        internal async Task<Dictionary<(long From, long To), int>> BuildAsync(
            IEnumerable<long> addressIds,
            CancellationToken cancellationToken = default)
        {
            var addressIdList = addressIds
                .Distinct()
                .ToList();

            if (addressIdList.Count == 0)
            {
                return new Dictionary<(long From, long To), int>();
            }

            try
            {
                await _routeEvaluationService.EnsureAllPairwiseRoutesAsync(addressIdList, cancellationToken);
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "route.preview.ensure-pairwise.failed addressCount={AddressCount}",
                    addressIdList.Count);
            }

            return await _travelResolver.GetTravelMinutesAsync(addressIdList, cancellationToken);
        }
    }
}
