using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FollowUp.Core.Data;

/// <summary>Database bootstrap: migrations on SQL Server (with retry while SQL Server is still starting), EnsureCreated on SQLite.</summary>
public static class Database
{
    public const string DefaultConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=FollowUpOfficer;Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>
    /// Applies migrations. Retries for up to ~2 minutes so the service survives being started before SQL Server
    /// is ready (e.g. at Windows boot — spec §59, §61).
    /// </summary>
    public static async Task MigrateAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
                if (db.IsSqlite) await db.Database.EnsureCreatedAsync(ct);
                else await db.Database.MigrateAsync(ct);
                return;
            }
            catch (Exception ex) when (attempt < 12 && !ct.IsCancellationRequested && IsTransient(ex))
            {
                logger.LogWarning(ex, "Database not ready (attempt {Attempt}); retrying in 10 s", attempt);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    public static bool IsTransient(Exception ex) =>
        ex is Microsoft.Data.SqlClient.SqlException or TimeoutException or System.Net.Sockets.SocketException
        || ex.InnerException is not null && IsTransient(ex.InnerException);

    /// <summary>True when the exception is a unique-index violation (idempotency guard hit).</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException switch
    {
        Microsoft.Data.SqlClient.SqlException sql => sql.Number is 2601 or 2627,
        Microsoft.Data.Sqlite.SqliteException lite => lite.SqliteErrorCode == 19,
        _ => false,
    };
}
