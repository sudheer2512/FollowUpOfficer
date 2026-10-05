using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace FollowUp.Core.Platform;

/// <summary>Host-level configuration helpers shared by the API and the Worker.</summary>
public static class HostDatabase
{
    /// <summary>
    /// "Database:Provider" = "Sqlite" switches to SQLite (development smoke runs, tests); SQL Server otherwise.
    /// A relative SQLite file path resolves against the content root and its folder is created.
    /// </summary>
    public static bool UseSqlite(IConfiguration config, string contentRoot, ref string? connectionString)
    {
        if (!string.Equals(config["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase)) return false;
        var b = new SqliteConnectionStringBuilder(string.IsNullOrWhiteSpace(connectionString) ? "Data Source=data/followup.db" : connectionString);
        var inMemory = b.Mode == SqliteOpenMode.Memory || b.DataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase)
            || b.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!inMemory)
        {
            if (!Path.IsPathRooted(b.DataSource)) b.DataSource = Path.GetFullPath(Path.Combine(contentRoot, b.DataSource));
            if (Path.GetDirectoryName(b.DataSource) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        }
        connectionString = b.ToString();
        return true;
    }

    /// <summary>
    /// Makes relative data folders (Data Protection keys, attachment storage) absolute under the content root — a Windows
    /// service runs with System32 as its working directory (spec §59).
    /// </summary>
    public static void NormalisePaths(IConfiguration config, string contentRoot)
    {
        foreach (var key in new[] { "DataProtection:KeysPath", "FollowUp:StoragePath" })
            if (config[key] is { Length: > 0 } value && !Path.IsPathRooted(value))
                config[key] = Path.GetFullPath(Path.Combine(contentRoot, value));
    }
}
