using FollowUp.Integrations.Graph;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations.GraphEmulator;

/// <summary>Startup helper for the emulator store.</summary>
public static class GraphEmulatorDatabase
{
    /// <summary>
    /// Applies the emulator migrations (SQL Server) or creates the emulator tables (SQLite — also when the database file
    /// already holds the application's tables), then creates the settings row and the system users. No-op unless
    /// Microsoft365:Mode is Emulator.
    /// </summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var options = services.GetRequiredService<IOptions<GraphOptions>>().Value;
        if (options.Mode != Microsoft365Mode.Emulator) return;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GraphEmulatorDbContext>();
        if (db.Database.IsSqlite())
        {
            var creator = db.GetService<IRelationalDatabaseCreator>();
            if (!await creator.ExistsAsync(ct)) await creator.CreateAsync(ct);
            if (!await SqliteTableExistsAsync(db, GraphEmulatorDbContext.Schema + "_Settings", ct)) await creator.CreateTablesAsync(ct);
        }
        else
        {
            await db.Database.MigrateAsync(ct);
        }

        await scope.ServiceProvider.GetRequiredService<GraphEmulatorStore>().EnsureInitializedAsync(ct);
    }

    private static async Task<bool> SqliteTableExistsAsync(GraphEmulatorDbContext db, string table, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            command.Parameters.Add(new SqliteParameter("$name", table));
            var count = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            return count > 0;
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
    }
}
