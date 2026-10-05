using FollowUp.Core;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Core.Platform;
using FollowUp.Core.Services;
using FollowUp.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Tests.Core.Queries;

/// <summary>
/// Core services over an in-memory SQLite database (a kept-open connection holds the shared-cache database alive),
/// with a pinned clock and the fallback ports. Each test gets its own database.
/// Set FOLLOWUP_TEST_SQLSERVER to a SQL Server connection string (no database name) to run the same tests against
/// throwaway SQL Server databases instead — they are created per test and dropped afterwards.
/// </summary>
public sealed class CoreTestHost : IAsyncDisposable
{
    private readonly SqliteConnection? _keeper;

    public ServiceProvider Services { get; }
    public MutableClock Clock { get; }
    public TestOrg Org { get; } = TestOrg.Default;

    private CoreTestHost(SqliteConnection? keeper, ServiceProvider services, MutableClock clock)
    {
        _keeper = keeper;
        Services = services;
        Clock = clock;
    }

    /// <summary>A unique database: SQL Server when FOLLOWUP_TEST_SQLSERVER is set, otherwise shared-cache in-memory SQLite.</summary>
    public static (string ConnectionString, bool Sqlite, SqliteConnection? Keeper) NewDatabase(string prefix)
    {
        if (Environment.GetEnvironmentVariable("FOLLOWUP_TEST_SQLSERVER") is { Length: > 0 } sql)
        {
            var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sql) { InitialCatalog = $"FollowUpTest_{prefix}_{Guid.NewGuid():N}" };
            return (b.ConnectionString, false, null);
        }
        var cs = $"Data Source=fu-{prefix}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keeper = new SqliteConnection(cs);
        keeper.Open();
        return (cs, true, keeper);
    }

    public static async Task DropSqlServerDatabaseAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<FollowUpDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new FollowUpDbContext(options, new SystemClock());
        await db.Database.EnsureDeletedAsync();
    }

    public static async Task<CoreTestHost> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var (cs, sqlite, keeper) = NewDatabase("core");
        var temp = Path.Combine(Path.GetTempPath(), "followup-tests");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jobs:Enabled"] = "false",
            ["FollowUp:SeedDemoData"] = "false",
            ["FollowUp:StoragePath"] = Path.Combine(temp, "attachments"),
            ["DataProtection:KeysPath"] = Path.Combine(temp, "keys"),
            ["Microsoft365:Mode"] = "Disabled",
        }).Build();

        var clock = new MutableClock();
        clock.Pin(TestOrg.Now);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSingleton<IClock>(clock);
        services.AddFollowUpCore(config, "Test", cs, useSqlite: sqlite);
        configure?.Invoke(services);
        services.AddFallbackPorts();
        var sp = services.BuildServiceProvider();

        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
            await db.Database.EnsureCreatedAsync();
            await TestOrg.Default.SeedAsync(db);
        }
        return new CoreTestHost(keeper, sp, clock);
    }

    /// <summary>A scope acting as the given employee (roles and team scope from the database).</summary>
    public async Task<AsyncServiceScope> AsAsync(Guid employeeId)
    {
        var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
        var account = await db.UserAccounts.AsNoTracking().FirstAsync(a => a.EmployeeId == employeeId);
        var name = await db.Employees.Where(e => e.Id == employeeId).Select(e => e.DisplayName).FirstAsync();
        var viewer = await ViewerFactory.BuildAsync(db, employeeId, account.Roles, CancellationToken.None);
        scope.ServiceProvider.GetRequiredService<ActorContext>().SetUser(employeeId, name, viewer);
        return scope;
    }

    /// <summary>Creates a task as <paramref name="by"/> (CEO by default).</summary>
    public async Task<WorkTask> CreateTaskAsync(CreateTaskRequest request, Guid? by = null)
    {
        await using var scope = await AsAsync(by ?? Org.Ceo);
        return await scope.ServiceProvider.GetRequiredService<TaskService>().CreateAsync(request, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        string? sqlServer = null;
        if (_keeper is null)
        {
            await using var scope = Services.CreateAsyncScope();
            sqlServer = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>().Database.GetConnectionString();
        }
        await Services.DisposeAsync();
        if (_keeper is not null) await _keeper.DisposeAsync();
        if (sqlServer is not null) await DropSqlServerDatabaseAsync(sqlServer);
    }
}
