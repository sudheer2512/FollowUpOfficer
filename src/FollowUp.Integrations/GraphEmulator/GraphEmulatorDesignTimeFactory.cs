using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FollowUp.Integrations.GraphEmulator;

/// <summary>Design-time factory for "dotnet ef migrations" (SQL Server LocalDB).</summary>
public sealed class GraphEmulatorDesignTimeFactory : IDesignTimeDbContextFactory<GraphEmulatorDbContext>
{
    public const string DefaultConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=FollowUpOfficer;Trusted_Connection=True;TrustServerCertificate=True";

    public GraphEmulatorDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<GraphEmulatorDbContext>();
        GraphEmulatorDbContext.Configure(builder, DefaultConnectionString, useSqlite: false);
        return new GraphEmulatorDbContext(builder.Options);
    }
}
