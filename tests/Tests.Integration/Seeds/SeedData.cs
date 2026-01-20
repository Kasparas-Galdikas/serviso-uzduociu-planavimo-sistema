using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ServiceTaskPlanningSystem.Data.Seed;

namespace Tests.Integration.Seeds;

/// <summary>
/// Reuses the application's DatabaseSeeder to populate baseline data for integration tests.
/// </summary>
public static class SeedData
{
    public static async Task EnsureSeedDataAsync(IServiceScope scope)
    {
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedAsync();
    }
}
