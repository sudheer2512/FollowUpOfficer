using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FollowUp.Core.Data;

/// <summary>Used by `dotnet ef migrations add … --project src/FollowUp.Core` only.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<FollowUpDbContext>
{
    public FollowUpDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FollowUpDbContext>()
            .UseSqlServer(Database.DefaultConnectionString, sql => sql.MigrationsAssembly(typeof(FollowUpDbContext).Assembly.FullName))
            .Options;
        return new FollowUpDbContext(options, new SystemClock());
    }
}
