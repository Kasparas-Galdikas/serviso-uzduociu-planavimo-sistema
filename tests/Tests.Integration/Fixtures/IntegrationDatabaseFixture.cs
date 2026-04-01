using System;
using System.Threading.Tasks;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tests.Integration.Fixtures;

/// <summary>
/// Provides a PostgreSQL connection for integration tests.
/// Prefers an explicit connection string and falls back to Testcontainers when Docker is available.
/// </summary>
public sealed class IntegrationDatabaseFixture : IAsyncLifetime
{
    private const string ConnectionStringEnvVar = "IntegrationTests__ConnectionString";

    private readonly PostgreSqlContainer? _container;

    public IntegrationDatabaseFixture()
    {
        var externalConnectionString = ResolveExternalConnectionString();
        if (externalConnectionString != null)
        {
            ConnectionString = externalConnectionString;
            return;
        }

        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("servisotest")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithCleanUp(true)
            .Build();
    }

    public string ConnectionString { get; private set; } = string.Empty;

    public bool UsesContainer => _container != null;

    public async Task InitializeAsync()
    {
        if (_container == null)
        {
            return;
        }

        try
        {
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Integration tests require either a reachable Docker daemon or the {ConnectionStringEnvVar} environment variable " +
                "pointing at a dedicated PostgreSQL database.",
                ex);
        }

        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    private static string? ResolveExternalConnectionString()
    {
        var explicitConnection = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitConnection))
        {
            return explicitConnection;
        }

        var namedTestConnection = Environment.GetEnvironmentVariable("ConnectionStrings__IntegrationTests");
        if (!string.IsNullOrWhiteSpace(namedTestConnection))
        {
            return namedTestConnection;
        }

        return null;
    }
}
