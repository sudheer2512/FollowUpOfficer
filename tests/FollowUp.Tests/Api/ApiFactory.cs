using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FollowUp.Core.Data;
using FollowUp.Core.Platform;
using FollowUp.Domain;
using FollowUp.Tests.Core.Queries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Tests.Api;

/// <summary>
/// The real API host over an in-memory SQLite database, with background jobs, demo seeding and the real-time relay
/// switched off. The test organisation is seeded through the <see cref="IStartupSeeder"/> hook.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection? _keeper;
    private readonly string _connectionString;
    private readonly bool _sqlite;

    public static readonly JsonSerializerOptions Json = CreateJson();

    /// <summary>In-memory SQLite by default; a throwaway SQL Server database (migrated) when FOLLOWUP_TEST_SQLSERVER is set.</summary>
    public ApiFactory() => (_connectionString, _sqlite, _keeper) = CoreTestHost.NewDatabase("api");

    public MutableClock Clock { get; } = CreateClock();

    private static MutableClock CreateClock()
    {
        var c = new MutableClock();
        c.Pin(TestOrg.Now);
        return c;
    }

    private static JsonSerializerOptions CreateJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var temp = Path.Combine(Path.GetTempPath(), "followup-api-tests");
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:FollowUp", _connectionString);
        builder.UseSetting("Database:Provider", _sqlite ? "Sqlite" : "SqlServer");
        builder.UseSetting("Jobs:Enabled", "false");
        builder.UseSetting("FollowUp:SeedDemoData", "false");
        builder.UseSetting("Realtime:Enabled", "false");
        builder.UseSetting("Microsoft365:Mode", "Disabled");
        builder.UseSetting("DataProtection:KeysPath", Path.Combine(temp, "keys"));
        builder.UseSetting("FollowUp:StoragePath", Path.Combine(temp, "attachments"));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IStartupSeeder, TestOrgSeeder>();
        });
    }

    /// <summary>A client with a cookie jar; <paramref name="csrf"/> adds the X-Requested-With header.</summary>
    public HttpClient Client(bool csrf = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        if (csrf) client.DefaultRequestHeaders.Add("X-Requested-With", "FollowUp");
        return client;
    }

    public Task<HttpClient> SignInAsync(string userName) => SignInAsync(userName, TestOrg.Password);

    public async Task<HttpClient> SignInAsync(string userName, string password)
    {
        var client = Client();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName, password });
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sign-in as {userName} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _keeper?.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_keeper is not null) await _keeper.DisposeAsync();
        else await CoreTestHost.DropSqlServerDatabaseAsync(_connectionString);
    }

    private sealed class TestOrgSeeder : IStartupSeeder
    {
        public int Order => 0;

        public async Task SeedAsync(IServiceProvider sp, CancellationToken ct) =>
            await TestOrg.Default.SeedAsync(sp.GetRequiredService<FollowUpDbContext>(), ct);
    }
}
