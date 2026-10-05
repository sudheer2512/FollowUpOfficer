using System.Net.Http.Json;
using FollowUp.Core.Abstractions;
using FollowUp.Integrations;
using FollowUp.Integrations.GraphEmulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace FollowUp.Tests.Integrations;

/// <summary>
/// In-process API host with the Graph emulator (SQLite in-memory store) on a TestServer. The Graph services' HTTP
/// clients are wired to the TestServer, so the real Graph client code path talks to the emulator end to end.
/// </summary>
internal sealed class EmulatorHost : IAsyncDisposable
{
    public static readonly TenantUser Ceo = User("ceo@contoso.com", "Anita Rao", "Chief Executive Officer", "Management");
    public static readonly TenantUser Ravi = User("ravi@contoso.com", "Ravi Kumar", "Finance Manager", "Finance", Ceo.Id);
    public static readonly TenantUser Priya = User("priya@contoso.com", "Priya Shah", "Operations Lead", "Operations", Ceo.Id);
    public static readonly string OfficerId = GraphEmulatorIds.ForUser("followup@contoso.com");
    public static readonly string TeamId = GraphEmulatorIds.ForTeam("Operations");
    public static readonly string ChannelId = GraphEmulatorIds.ForChannel(TeamId, "General");
    public static readonly string GroupChatId = "19:leadership-group@thread.v2";

    /// <summary>
    /// Optional SQL Server connection string (without Database=) to run the emulator tests against SQL Server instead of
    /// SQLite, e.g. "Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True". Each host then
    /// migrates and finally drops its own uniquely named database.
    /// </summary>
    private const string SqlServerVariable = "FOLLOWUP_TEST_SQLSERVER";

    private readonly SqliteConnection? _keepAlive;

    private EmulatorHost(SqliteConnection? keepAlive, WebApplication app, string? kestrelBaseUrl)
    {
        _keepAlive = keepAlive;
        App = app;
        Console = kestrelBaseUrl is null ? app.GetTestClient() : new HttpClient();
        Console.BaseAddress = new Uri((kestrelBaseUrl ?? "http://localhost") + "/api/m365-sim/");
    }

    public WebApplication App { get; }

    /// <summary>Client for the simulator console API ("/api/m365-sim/").</summary>
    public HttpClient Console { get; }

    /// <summary>
    /// Starts the host on a TestServer, or (<paramref name="useKestrel"/>) on Kestrel at a free loopback port so the Graph
    /// services call the emulator over real HTTP exactly like the API process does in Emulator mode.
    /// </summary>
    public static async Task<EmulatorHost> StartAsync(Action<Dictionary<string, string?>>? configure = null, bool useKestrel = false)
    {
        var sqlServer = Environment.GetEnvironmentVariable(SqlServerVariable);
        var useSqlite = string.IsNullOrWhiteSpace(sqlServer);
        SqliteConnection? keepAlive = null;
        string connectionString;
        if (useSqlite)
        {
            connectionString = $"Data Source=emu-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            keepAlive = new SqliteConnection(connectionString);
            await keepAlive.OpenAsync();
        }
        else
        {
            connectionString = $"{sqlServer!.TrimEnd(';')};Database=FollowUpEmulatorTest_{Guid.NewGuid():N}";
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        string? kestrelBaseUrl = null;
        if (useKestrel)
        {
            kestrelBaseUrl = $"http://127.0.0.1:{FreePort()}";
            builder.WebHost.UseUrls(kestrelBaseUrl);
        }
        else
        {
            builder.WebHost.UseTestServer();
        }
        builder.Logging.ClearProviders();
        var settings = new Dictionary<string, string?>
        {
            ["Microsoft365:Mode"] = "Emulator",
            ["Microsoft365:PublicBaseUrl"] = kestrelBaseUrl ?? "http://localhost",
            ["Microsoft365:SenderMailbox"] = "followup@contoso.com",
            ["Microsoft365:MaxRetries"] = "2",
            ["Microsoft365:RetryBaseDelaySeconds"] = "0.01",
            ["Microsoft365:MaxRetryDelaySeconds"] = "0.05",
        };
        configure?.Invoke(settings);
        // Isolate from the API's appsettings.json, which is copied into the test output (the test project references the API).
        builder.Configuration.Sources.Clear();
        if (kestrelBaseUrl is not null) settings["urls"] = kestrelBaseUrl;   // UseUrls is stored in configuration
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddMicrosoft365Integration(builder.Configuration, connectionString, useSqlite);
        if (!useKestrel)
            builder.Services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b =>
                b.PrimaryHandler = ((TestServer)b.Services.GetRequiredService<IServer>()).CreateHandler()));

        var app = builder.Build();
        app.MapGraphEmulator();
        app.MapGroup("/api/m365-sim").MapGraphEmulatorConsole();
        await GraphEmulatorDatabase.MigrateAsync(app.Services);
        await app.StartAsync();
        return new EmulatorHost(keepAlive, app, kestrelBaseUrl);
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Starts a host and seeds the standard directory (CEO, Ravi, Priya, the Operations team and a group chat).</summary>
    public static async Task<EmulatorHost> StartSeededAsync(Action<Dictionary<string, string?>>? configure = null, bool useKestrel = false)
    {
        var host = await StartAsync(configure, useKestrel);
        await host.TenantAsync(t => t.EnsureDirectoryAsync(
            [Ceo, Ravi, Priya],
            [new TenantTeam(TeamId, "Operations", [new TenantChannel(ChannelId, "General")], [Ceo.Id, Ravi.Id, Priya.Id])],
            [new TenantChat(GroupChatId, "group", "Leadership", [Ceo.Id, Ravi.Id, Priya.Id, OfficerId])],
            CancellationToken.None));
        return host;
    }

    public T Get<T>() where T : notnull => App.Services.GetRequiredService<T>();

    public async Task<T> TenantAsync<T>(Func<IDemoTenant, Task<T>> action)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IDemoTenant>());
    }

    public async Task TenantAsync(Func<IDemoTenant, Task> action)
    {
        await using var scope = App.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<IDemoTenant>());
    }

    public Task SetFaultsAsync(TenantFaults faults) => TenantAsync(t => t.SetFaultsAsync(faults, CancellationToken.None));

    public async Task<T> GetJsonAsync<T>(string relativeUrl)
    {
        var response = await Console.GetAsync(relativeUrl);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public async Task<T> PostJsonAsync<T>(string relativeUrl, object body)
    {
        var response = await Console.PostAsJsonAsync(relativeUrl, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public static Task<(List<InboundMessage> Messages, SyncCursor Cursor, int Pages)> DrainAsync(IMailSynchronizationService sync, SyncCursor cursor) =>
        DrainAsync(c => sync.FetchAsync(c, CancellationToken.None), cursor);

    public static Task<(List<InboundMessage> Messages, SyncCursor Cursor, int Pages)> DrainAsync(ITeamsSynchronizationService sync, SyncCursor cursor) =>
        DrainAsync(c => sync.FetchAsync(c, CancellationToken.None), cursor);

    /// <summary>Fetches pages until the round is final, persisting the cursor values after every page like Core does.</summary>
    private static async Task<(List<InboundMessage> Messages, SyncCursor Cursor, int Pages)> DrainAsync(Func<SyncCursor, Task<SyncPage>> fetch,
        SyncCursor cursor)
    {
        var all = new List<InboundMessage>();
        for (var pages = 1; pages <= 50; pages++)
        {
            var page = await fetch(cursor);
            all.AddRange(page.Messages);
            cursor = cursor with
            {
                ContinuationToken = page.ContinuationToken,
                DeltaToken = page.DeltaToken ?? cursor.DeltaToken,
                Watermark = page.Watermark ?? cursor.Watermark,
            };
            if (page.IsFinal) return (all, cursor, pages);
            Assert.NotNull(page.ContinuationToken);
            Assert.Null(page.DeltaToken);
        }
        throw new InvalidOperationException("The sync round did not finish.");
    }

    public async ValueTask DisposeAsync()
    {
        Console.Dispose();
        await App.StopAsync();
        if (_keepAlive is null)
        {
            // SQL Server run: remove the per-host test database.
            await using var scope = App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<GraphEmulatorDbContext>().Database.EnsureDeletedAsync();
        }
        await App.DisposeAsync();
        if (_keepAlive is not null) await _keepAlive.DisposeAsync();
    }

    private static TenantUser User(string mail, string name, string title, string department, string? managerId = null) =>
        new(GraphEmulatorIds.ForUser(mail), name, mail, title, department, managerId);
}
