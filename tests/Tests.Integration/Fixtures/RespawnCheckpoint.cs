using System.Threading.Tasks;
using Respawn;

namespace Tests.Integration.Fixtures;

/// <summary>
/// Resets the database to a clean state between tests.
/// </summary>
public sealed class RespawnCheckpoint
{
    private Respawner? _respawner;

    public async Task ResetAsync(string connectionString)
    {
        _respawner ??= await Respawner.CreateAsync(connectionString, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = new[] { "public" },
            WithReseed = true
        });

        await _respawner.ResetAsync(connectionString);
    }
}
