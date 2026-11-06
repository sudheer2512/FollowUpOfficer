using System.Net;
using System.Net.Http.Json;
using FollowUp.Core.Data;
using FollowUp.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Tests.Api;

/// <summary>The "Bootstrap" settings give a clean install (no demo data, no users) its first administrator.</summary>
public sealed class FirstAdministratorTests
{
    private const string UserName = "firstadmin";
    private const string Password = "Str0ng-first-admin";

    private static WebApplicationFactory<Program> Host(ApiFactory api, string? password = Password, bool emptyOrganisation = true) => api.WithWebHostBuilder(b =>
    {
        b.UseSetting("Bootstrap:AdminUserName", UserName);
        b.UseSetting("Bootstrap:AdminEmail", "first.admin@example.test");
        if (password is not null) b.UseSetting("Bootstrap:AdminPassword", password);
        if (emptyOrganisation)
            b.ConfigureTestServices(services =>
            {
                foreach (var seeder in services.Where(d => d.ImplementationType?.Name == "TestOrgSeeder").ToList()) services.Remove(seeder);
            });
    });

    private static HttpClient Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Requested-With", "FollowUp");
        return client;
    }

    private static async Task<HttpStatusCode> SignInAsync(HttpClient client, string password) =>
        (await client.PostAsJsonAsync("/api/auth/login", new { userName = UserName, password })).StatusCode;

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<FollowUpDbContext, Task<T>> query)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<FollowUpDbContext>());
    }

    [Fact]
    public async Task Clean_install_gets_an_administrator_who_can_sign_in_and_manage_users()
    {
        await using var api = new ApiFactory();
        var host = Host(api);
        using var client = Client(host);

        Assert.Equal(HttpStatusCode.OK, await SignInAsync(client, Password));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/employees")).StatusCode);
        var account = await QueryAsync(host, db => db.UserAccounts.SingleAsync());
        Assert.Equal(UserName, account.UserName);
        Assert.True(account.Roles.HasFlag(AppRole.Administrator));
        Assert.NotEqual(Password, account.PasswordHash);
    }

    [Fact]
    public async Task A_restart_never_changes_the_existing_administrator()
    {
        await using var api = new ApiFactory();
        using var first = Client(Host(api));
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(first, Password));

        var restarted = Host(api, password: "Different-pass-123");
        using var second = Client(restarted);

        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(second, "Different-pass-123"));
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(second, Password));
        Assert.Equal(1, await QueryAsync(restarted, db => db.UserAccounts.CountAsync()));
    }

    [Fact]
    public async Task An_organisation_with_an_administrator_is_left_alone()
    {
        await using var api = new ApiFactory();
        var host = Host(api, emptyOrganisation: false);
        using var client = Client(host);

        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(client, Password));
        Assert.False(await QueryAsync(host, db => db.UserAccounts.AnyAsync(a => a.UserName == UserName)));
    }

    [Fact]
    public async Task A_disabled_administrator_stops_a_leftover_bootstrap_section_creating_another()
    {
        await using var api = new ApiFactory();
        var first = Host(api);
        using var firstClient = Client(first);
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(firstClient, Password));

        // New values in every unique field, so only the administrator check can stop a second account.
        await QueryAsync(first, db => db.UserAccounts.ExecuteUpdateAsync(s => s
            .SetProperty(a => a.IsEnabled, false)
            .SetProperty(a => a.UserName, "former.admin")));
        await QueryAsync(first, db => db.Employees.ExecuteUpdateAsync(s => s
            .SetProperty(e => e.EmployeeCode, "E-0001")
            .SetProperty(e => e.Email, "former.admin@example.test")));

        var restarted = Host(api);
        using var client = Client(restarted);

        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(client, Password));
        Assert.Equal(1, await QueryAsync(restarted, db => db.UserAccounts.CountAsync()));
    }

    [Theory]
    [InlineData("short")]
    [InlineData(null)]
    public async Task A_short_or_missing_password_creates_nobody(string? password)
    {
        await using var api = new ApiFactory();
        var host = Host(api, password);
        using var client = Client(host);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(0, await QueryAsync(host, db => db.UserAccounts.CountAsync()));
        Assert.Equal(0, await QueryAsync(host, db => db.Employees.CountAsync()));
    }
}
