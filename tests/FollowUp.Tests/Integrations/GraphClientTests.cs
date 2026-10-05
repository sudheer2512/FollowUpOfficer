using System.Diagnostics;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Tests.Integrations;

public sealed class GraphClientTests
{
    private static object UsersPage(string? nextLink, params string[] ids)
    {
        var value = ids.Select(id => new { id, displayName = "User " + id, mail = id + "@contoso.com", accountEnabled = true }).ToArray();
        return nextLink is null
            ? new Dictionary<string, object> { ["value"] = value }
            : new Dictionary<string, object> { ["value"] = value, ["@odata.nextLink"] = nextLink };
    }

    [Fact]
    public async Task Retries_429_honouring_Retry_After_then_succeeds()
    {
        var handler = new FakeGraphHandler()
            .Enqueue(() => FakeGraphHandler.GraphError(429, "TooManyRequests", "Slow down", ("Retry-After", "1")))
            .Enqueue(() => FakeGraphHandler.Json(200, UsersPage(null, "u1")));
        await using var sp = GraphTestServices.Create(handler, s => s["Microsoft365:MaxRetryDelaySeconds"] = "5");

        var watch = Stopwatch.StartNew();
        var users = await sp.GetRequiredService<IUserDirectoryService>().ListUsersAsync(CancellationToken.None);

        Assert.Single(users);
        Assert.Equal(2, handler.Calls.Count);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900), $"waited only {watch.Elapsed}");
        Assert.All(handler.Calls, c => Assert.Equal("Bearer token-1", c.Authorization));
    }

    [Fact]
    public async Task Exhausted_throttling_is_reported_with_retry_after()
    {
        var handler = new FakeGraphHandler { Default = _ => FakeGraphHandler.GraphError(429, "TooManyRequests", "Slow down", ("Retry-After", "7")) };
        await using var sp = GraphTestServices.Create(handler, s => s["Microsoft365:MaxRetries"] = "1");

        var ex = await Assert.ThrowsAsync<IntegrationException>(() =>
            sp.GetRequiredService<IUserDirectoryService>().ListUsersAsync(CancellationToken.None));

        Assert.Equal(IntegrationErrorKind.Throttled, ex.Kind);
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
        Assert.True(ex.IsTransient);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData(401, "InvalidAuthenticationToken", IntegrationErrorKind.Authentication)]
    [InlineData(403, "Authorization_RequestDenied", IntegrationErrorKind.Permission)]
    [InlineData(404, "ErrorItemNotFound", IntegrationErrorKind.NotFound)]
    [InlineData(410, "Gone", IntegrationErrorKind.Gone)]
    [InlineData(400, "syncStateNotFound", IntegrationErrorKind.Gone)]
    [InlineData(400, "resyncRequired", IntegrationErrorKind.Gone)]
    [InlineData(500, "InternalServerError", IntegrationErrorKind.Transient)]
    [InlineData(400, "BadRequest", IntegrationErrorKind.Validation)]
    [InlineData(409, "Conflict", IntegrationErrorKind.Validation)]
    public async Task Classifies_failures(int status, string code, IntegrationErrorKind expected)
    {
        var handler = new FakeGraphHandler { Default = _ => FakeGraphHandler.GraphError(status, code, "details " + code) };
        await using var sp = GraphTestServices.Create(handler);

        var ex = await Assert.ThrowsAsync<IntegrationException>(() =>
            sp.GetRequiredService<IUserDirectoryService>().ListUsersAsync(CancellationToken.None));

        Assert.Equal(expected, ex.Kind);
        Assert.Equal(status, ex.StatusCode);
        Assert.Contains(code, ex.Message);
        Assert.Contains("details " + code, ex.Message);
        if (status == 401)
        {
            // A rejected token is dropped and re-acquired once before the failure is reported.
            Assert.Equal(2, handler.TokenRequests);
            Assert.Equal(2, handler.Calls.Count);
        }
        else
        {
            Assert.Single(handler.Calls); // 4xx and 500 are not retried
        }
    }

    [Fact]
    public async Task Service_unavailable_is_retried_then_transient()
    {
        var handler = new FakeGraphHandler { Default = _ => FakeGraphHandler.GraphError(503, "ServiceUnavailable", "down") };
        await using var sp = GraphTestServices.Create(handler, s => s["Microsoft365:MaxRetries"] = "2");

        var ex = await Assert.ThrowsAsync<IntegrationException>(() =>
            sp.GetRequiredService<IUserDirectoryService>().ListUsersAsync(CancellationToken.None));

        Assert.Equal(IntegrationErrorKind.Transient, ex.Kind);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task Network_failure_is_retried_then_transient()
    {
        var handler = new FakeGraphHandler { Default = _ => throw new HttpRequestException("connection refused") };
        await using var sp = GraphTestServices.Create(handler, s => s["Microsoft365:MaxRetries"] = "1");

        var ex = await Assert.ThrowsAsync<IntegrationException>(() =>
            sp.GetRequiredService<IUserDirectoryService>().ListUsersAsync(CancellationToken.None));

        Assert.Equal(IntegrationErrorKind.Transient, ex.Kind);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Follows_next_links_and_reuses_the_cached_token()
    {
        var handler = new FakeGraphHandler()
            .Enqueue(() => FakeGraphHandler.Json(200, UsersPage("https://graph.test/v1.0/users?$skiptoken=page2", "u1", "u2")))
            .Enqueue(() => FakeGraphHandler.Json(200, UsersPage(null, "u3")));
        await using var sp = GraphTestServices.Create(handler);

        var users = await sp.GetRequiredService<IUserDirectoryService>().ListUsersAsync(CancellationToken.None);

        Assert.Equal(["u1", "u2", "u3"], users.Select(u => u.ExternalId));
        Assert.Equal(2, handler.Calls.Count);
        Assert.StartsWith("https://graph.test/v1.0/users?", handler.Calls[0].Uri.AbsoluteUri);
        Assert.Contains("$skiptoken=page2", handler.Calls[1].Uri.OriginalString);
        Assert.Equal(1, handler.TokenRequests);
    }

    [Fact]
    public async Task Refuses_to_send_tokens_to_a_foreign_next_link()
    {
        var handler = new FakeGraphHandler()
            .Enqueue(() => FakeGraphHandler.Json(200, UsersPage("https://evil.test/v1.0/users?$skiptoken=x", "u1")));
        await using var sp = GraphTestServices.Create(handler);

        var ex = await Assert.ThrowsAsync<IntegrationException>(() =>
            sp.GetRequiredService<IUserDirectoryService>().ListUsersAsync(CancellationToken.None));

        Assert.Equal(IntegrationErrorKind.Validation, ex.Kind);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Token_endpoint_rejection_is_an_authentication_error()
    {
        var handler = new FakeGraphHandler
        {
            TokenResponse = () => FakeGraphHandler.Json(401, new { error = "invalid_client", error_description = "AADSTS7000215: Invalid client secret." }),
        };
        await using var sp = GraphTestServices.Create(handler);

        var ex = await Assert.ThrowsAsync<IntegrationException>(() =>
            sp.GetRequiredService<IUserDirectoryService>().ListUsersAsync(CancellationToken.None));

        Assert.Equal(IntegrationErrorKind.Authentication, ex.Kind);
        Assert.Contains("invalid_client", ex.Message);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Missing_delegated_service_account_is_an_authentication_error()
    {
        var handler = new FakeGraphHandler();
        await using var sp = GraphTestServices.Create(handler, s => s["Microsoft365:ServiceAccountRefreshToken"] = "");

        var ex = await Assert.ThrowsAsync<IntegrationException>(() => sp.GetRequiredService<ITeamsService>().SendAsync(
            new OutboundTeamsMessage("k1", TeamsTargetKind.GroupChat, "Hello", ChatId: "19:x@thread.v2"), CancellationToken.None));

        Assert.Equal(IntegrationErrorKind.Authentication, ex.Kind);
        Assert.Contains("ServiceAccountRefreshToken", ex.Message);
    }

    [Fact]
    public async Task Send_mail_stamps_header_and_extended_property()
    {
        var handler = new FakeGraphHandler()
            .Enqueue(() => new HttpResponseMessage(System.Net.HttpStatusCode.Accepted))
            .Enqueue(() => FakeGraphHandler.Json(200, new { value = new[] { new { id = "AAMk1", internetMessageId = "<abc@contoso.com>" } } }));
        await using var sp = GraphTestServices.Create(handler);

        var result = await sp.GetRequiredService<IEmailService>().SendAsync(new OutboundEmail("key-'1'",
            [new EmailAddress("ravi@contoso.com", "Ravi")], [], "Reminder", "Please update", HighImportance: true), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("<abc@contoso.com>", result.ExternalMessageId);
        var send = handler.Calls[0];
        Assert.Equal(HttpMethod.Post, send.Method);
        Assert.Contains("/v1.0/users/followup", send.Uri.AbsoluteUri);
        Assert.EndsWith("/sendMail", send.Uri.AbsolutePath);
        Assert.Contains("\"X-FollowUp-Key\"", send.Body);
        Assert.Contains("String {00020329-0000-0000-C000-000000000046} Name FollowUpKey", send.Body);
        Assert.Contains("\"importance\":\"high\"", send.Body);
        var lookup = Uri.UnescapeDataString(handler.Calls[1].Uri.Query);
        Assert.Contains("ep/value eq 'key-''1'''", lookup);
        Assert.Contains("sentDateTime ge ", lookup);
    }
}
