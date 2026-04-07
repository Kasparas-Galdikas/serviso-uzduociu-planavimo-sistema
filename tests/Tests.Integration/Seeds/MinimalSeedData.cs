using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ServiceTaskPlanningSystem.Data.Seed;

namespace Tests.Integration.Seeds;

/// <summary>
/// Seeds only reference data (users, technicians, service objects) with no plans or tasks.
/// Use in tests that assert exact task/backlog counts and need a clean slate.
/// </summary>
public static class MinimalSeedData
{
    public static async Task EnsureSeedDataAsync(IServiceScope scope)
    {
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedReferenceDataOnlyAsync();
    }
}
