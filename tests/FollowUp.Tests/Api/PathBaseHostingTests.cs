using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FollowUp.Tests.Api;

/// <summary>
/// IIS virtual-application / reverse-proxy sub-path hosting: the UI's &lt;base href&gt; follows the path base, deep links
/// survive a refresh, static assets and the API resolve under the prefix, and unknown API paths stay 404.
/// </summary>
public sealed class PathBaseHostingTests : IDisposable
{
    private const string IndexHtml = "<!doctype html><html><head><base href=\"/\"><script src=\"main.js\"></script></head><body><app-root></app-root></body></html>";
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "followup-webroot-" + Guid.NewGuid().ToString("N"));

    public PathBaseHostingTests()
    {
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), IndexHtml);
        File.WriteAllText(Path.Combine(_webRoot, "main.js"), "console.log('ui');");
    }

    public void Dispose()
    {
        try { Directory.Delete(_webRoot, recursive: true); } catch (IOException) { }
    }

    private WebApplicationFactory<Program> Host(ApiFactory api, string? pathBase, string? publicAppUrl = null) => api.WithWebHostBuilder(b =>
    {
        b.UseSetting(WebHostDefaults.WebRootKey, _webRoot);
        if (pathBase is not null) b.UseSetting("PathBase", pathBase);
        if (publicAppUrl is not null) b.UseSetting("FollowUp:PublicAppUrl", publicAppUrl);
    });

    [Theory]
    [InlineData("/FollowUpOfficer", "/followupofficer")]
    [InlineData("/FollowUpOfficer/", "/followupofficer/")]
    [InlineData("/FOLLOWUPOFFICER/tasks/42?tab=links", "/followupofficer/tasks/42?tab=links")]
    [InlineData("/FOLLOWUPOFFICER/a%3Fb%20c", "/followupofficer/a%3Fb%20c")]
    [InlineData("/FOLLOWUPOFFICER/a%0d%0ab", "/followupofficer/a%0D%0Ab")]
    public async Task Another_spelling_of_the_virtual_path_redirects_to_the_public_url(string url, string expected)
    {
        await using var api = new ApiFactory();
        using var client = Host(api, "/followupofficer", "http://localhost/followupofficer").CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_public_spelling_of_the_virtual_path_is_served_directly()
    {
        await using var api = new ApiFactory();
        using var client = Host(api, "/followupofficer", "http://localhost/followupofficer/").CreateClient(new() { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/followupofficer")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/followupofficer/tasks/42")).StatusCode);
    }

    [Fact]
    public async Task Writes_are_not_redirected_to_another_spelling()
    {
        await using var api = new ApiFactory();
        using var client = Host(api, "/followupofficer", "http://localhost/followupofficer").CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Requested-With", "FollowUp");

        var response = await client.PostAsJsonAsync("/FollowUpOfficer/api/auth/login", new { userName = "nobody", password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_public_virtual_path_every_spelling_is_served()
    {
        await using var api = new ApiFactory();
        using var client = Host(api, "/followupofficer", "http://localhost:5860").CreateClient(new() { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/FollowUpOfficer/tasks/42")).StatusCode);
    }

    [Theory]
    [InlineData("/followup")]
    [InlineData("/followup/")]
    [InlineData("/followup/tasks/42")]
    [InlineData("/followup/index.html")]
    public async Task Ui_under_a_virtual_path_gets_a_matching_base_href(string url)
    {
        await using var api = new ApiFactory();
        using var client = Host(api, "/followup").CreateClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<base href=\"/followup/\">", html);
        Assert.DoesNotContain("<base href=\"/\">", html);
    }

    [Fact]
    public async Task Static_assets_and_api_resolve_under_the_virtual_path()
    {
        await using var api = new ApiFactory();
        using var client = Host(api, "/followup").CreateClient();

        var script = await client.GetAsync("/followup/main.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("console.log", await script.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/followup/api/health")).StatusCode);
        var unknown = await client.GetAsync("/followup/api/no-such-route");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Ui_at_the_site_root_keeps_the_root_base_href()
    {
        await using var api = new ApiFactory();
        using var client = Host(api, pathBase: null).CreateClient();

        var html = await client.GetStringAsync("/tasks/42");

        Assert.Contains("<base href=\"/\">", html);
    }
}
