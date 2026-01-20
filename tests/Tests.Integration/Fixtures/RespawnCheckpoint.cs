using System.Threading.Tasks;
using Npgsql;
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
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            _respawner ??= await Respawner.CreateAsync(conn, new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                SchemasToInclude = ["public"],
                TablesToIgnore = [new Respawn.Graph.Table("__EFMigrationsHistory")],
                WithReseed = true
            });

            await _respawner.ResetAsync(conn);
        }
    }
