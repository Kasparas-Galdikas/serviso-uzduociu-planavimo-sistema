using Microsoft.Extensions.DependencyInjection;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;

namespace ServiceTaskPlanningSystem.Services.PlanManagement;

public static class PlanManagementServiceExtensions
{
    /// <summary>
    /// Registers internal PlanManagement infrastructure services that cannot be exposed
    /// through IPlanService (e.g. background-service-resolved internal dependencies).
    /// Call this from Program.cs instead of importing Orchestrator.Shared directly.
    /// </summary>
    public static IServiceCollection AddPlanManagementInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<PlanScopeSegregationService>();
        return services;
    }
}
