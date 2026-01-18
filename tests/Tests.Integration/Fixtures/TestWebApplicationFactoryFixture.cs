using System.Threading.Tasks;
using Xunit;

namespace Tests.Integration.Fixtures;

/// <summary>
/// Provides a shared TestWebApplicationFactory instance bound to the container fixture.
/// </summary>
public sealed class TestWebApplicationFactoryFixture : IAsyncLifetime
{
    public PostgresContainerFixture Postgres { get; } = new();
    public TestWebApplicationFactory Factory { get; private set; } = default!;
    public RespawnCheckpoint Respawn { get; } = new();

    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();
        Factory = new TestWebApplicationFactory(Postgres);
    }

    public async Task DisposeAsync()
    {
        Factory.Dispose();
        await Postgres.DisposeAsync();
    }
}
