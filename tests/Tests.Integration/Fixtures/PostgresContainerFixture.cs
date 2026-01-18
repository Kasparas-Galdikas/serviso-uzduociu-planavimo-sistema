using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using ServiceTaskPlanningSystem.Data;
using Xunit;

namespace Tests.Integration.Fixtures;

/// <summary>
/// Spins up a disposable PostgreSQL container and applies migrations.
/// Shared across integration tests to avoid per-test container cost.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;

    public PostgresContainerFixture()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("servisotest")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithCleanUp(true)
            .Build();
    }

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyMigrationsAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    private async Task ApplyMigrationsAsync()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseNpgsql(ConnectionString, npgsqlOptions =>
            {
                npgsqlOptions.MapEnum<UserRole>();
                npgsqlOptions.MapEnum<PlanStatus>();
                npgsqlOptions.MapEnum<TaskPriority>();
                npgsqlOptions.MapEnum<ScheduleTaskStatus>("task_status");
                npgsqlOptions.MapEnum<RiskLevel>();
                npgsqlOptions.MapEnum<LoadState>();
            })
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var db = new ScheduleDbContext(options);
        await db.Database.MigrateAsync();
    }
}
