using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using Xunit;

namespace Tests.Integration.Fixtures;

/// <summary>
/// Provides a shared TestWebApplicationFactory instance bound to the container fixture.
/// </summary>
public sealed class TestWebApplicationFactoryFixture : IAsyncLifetime
{
    public IntegrationDatabaseFixture Database { get; } = new();
    public IntegrationDatabaseFixture Postgres => Database;
    public TestWebApplicationFactory Factory { get; private set; } = default!;
    public RespawnCheckpoint Respawn { get; } = new();

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        Factory = new TestWebApplicationFactory(
            Database,
            configureTestServices: services =>
            {
                services.RemoveAll<ILocationApiClient>();
                services.RemoveAll<IDistanceApiClient>();
                services.AddSingleton<ILocationApiClient, NoNetworkLocationApiClient>();
                services.AddSingleton<IDistanceApiClient, NoNetworkDistanceApiClient>();
            },
            configOverrides: new Dictionary<string, string?>
            {
                ["HEIGIT_API_KEY"] = "__PLACEHOLDER__",
                ["HEIGIT_BASE_URL"] = "https://tests.invalid"
            });

        // Force the app to boot, which runs migrations via Program.cs
        // This must happen before Respawn can analyze the schema
        _ = Factory.Services;
    }

    public async Task DisposeAsync()
    {
        Factory.Dispose();
        await Database.DisposeAsync();
    }

    private sealed class NoNetworkLocationApiClient : ILocationApiClient
    {
        public Task<GeoPoint> GeocodeAsync(string address, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("External geocoding is disabled in integration tests.");
    }

    private sealed class NoNetworkDistanceApiClient : IDistanceApiClient
    {
        public Task<MatrixResult> GetMatrixAsync(IReadOnlyList<GeoPoint> coordinates, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("External distance matrix is disabled in integration tests.");
    }
}
