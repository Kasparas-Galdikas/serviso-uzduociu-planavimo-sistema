using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceTaskPlanningSystem.Data;

namespace Tests.Integration.Fixtures;

/// <summary>
/// Boots the API for integration tests, overriding DB and auth for test purposes.
/// </summary>
public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly PostgresContainerFixture _postgres;

    public TestWebApplicationFactory(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                options.DefaultChallengeScheme = TestAuthHandler.Scheme;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });

            services.RemoveAll(typeof(ScheduleDbContext));
            services.AddDbContext<ScheduleDbContext>(options =>
                options.UseNpgsql(_postgres.ConnectionString, npgsqlOptions =>
                {
                    npgsqlOptions.MapEnum<UserRole>();
                    npgsqlOptions.MapEnum<PlanStatus>();
                    npgsqlOptions.MapEnum<TaskPriority>();
                    npgsqlOptions.MapEnum<ScheduleTaskStatus>("task_status");
                    npgsqlOptions.MapEnum<RiskLevel>();
                    npgsqlOptions.MapEnum<LoadState>();
                }).UseSnakeCaseNamingConvention());
        });

        return base.CreateHost(builder);
    }
}
