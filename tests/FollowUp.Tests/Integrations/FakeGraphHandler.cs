using System.Net;
using System.Text;
using System.Text.Json;
using FollowUp.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace FollowUp.Tests.Integrations;

/// <summary>Scripted HTTP handler standing in for Entra ID and Graph: token requests always succeed unless scripted otherwise.</summary>
internal sealed class FakeGraphHandler : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

    private readonly Queue<Func<Call, HttpResponseMessage>> _responses = new();

    public List<Call> Calls { get; } = [];
    public int TokenRequests { get; private set; }
    public Func<HttpResponseMessage>? TokenResponse { get; set; }
    public Func<Call, HttpResponseMessage>? Default { get; set; }

    public FakeGraphHandler Enqueue(Func<Call, HttpResponseMessage> response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public FakeGraphHandler Enqueue(Func<HttpResponseMessage> response) => Enqueue(_ => response());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
        {
            TokenRequests++;
            return TokenResponse?.Invoke()
                   ?? Json(200, new { access_token = $"token-{TokenRequests}", expires_in = 3600, token_type = "Bearer" });
        }
        var call = new Call(request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), body);
        Calls.Add(call);
        if (_responses.Count > 0) return _responses.Dequeue()(call);
        return Default?.Invoke(call) ?? Json(200, new { value = Array.Empty<object>() });
    }

    public static HttpResponseMessage Json(int status, object body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    public static HttpResponseMessage GraphError(int status, string code, string message, params (string Name, string Value)[] headers) =>
        Json(status, new { error = new { code, message } }, headers);
}

/// <summary>Service provider in Graph mode whose HTTP traffic goes to a <see cref="FakeGraphHandler"/>.</summary>
internal static class GraphTestServices
{
    public static ServiceProvider Create(FakeGraphHandler handler, Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Microsoft365:Mode"] = "Graph",
            ["Microsoft365:TenantId"] = "tenant",
            ["Microsoft365:ClientId"] = "client",
            ["Microsoft365:ClientSecret"] = "secret",
            ["Microsoft365:GraphBaseUrl"] = "https://graph.test/v1.0",
            ["Microsoft365:AuthorityHost"] = "https://login.test",
            ["Microsoft365:SenderMailbox"] = "followup@contoso.com",
            ["Microsoft365:ServiceAccountRefreshToken"] = "refresh",
            ["Microsoft365:MaxRetries"] = "3",
            ["Microsoft365:RetryBaseDelaySeconds"] = "0.01",
            ["Microsoft365:MaxRetryDelaySeconds"] = "0.05",
        };
        configure?.Invoke(settings);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddMicrosoft365Integration(configuration, "");
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = handler));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
