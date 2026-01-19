using Testcontainers.PostgreSql;
using Xunit;

namespace Tests.Integration.Fixtures;

/// <summary>
/// Spins up a disposable PostgreSQL container for integration tests.
/// Migrations are applied by the app's Program.cs when the TestWebApplicationFactory boots.
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
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
