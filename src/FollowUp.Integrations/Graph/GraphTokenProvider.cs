using System.Net.Http.Json;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations.Graph;

/// <summary>Which identity a Graph call runs as.</summary>
public enum GraphAuth
{
    /// <summary>App-only token (client credentials).</summary>
    Application,
    /// <summary>Delegated token of the service account (refresh-token flow) — required to post Teams chat / channel messages.</summary>
    ServiceAccount,
}

/// <summary>Credentials used to acquire Graph tokens.</summary>
public sealed record GraphCredentials(string? TenantId, string? ClientId, string? ClientSecret, string? ServiceAccountUpn, string? RefreshToken);

/// <summary>
/// Supplies Graph credentials. The default reads <see cref="GraphOptions"/>; the host can replace it to read the encrypted
/// IntegrationAccount record and persist rotated refresh tokens.
/// </summary>
public interface IGraphCredentialSource
{
    ValueTask<GraphCredentials> GetCredentialsAsync(CancellationToken ct);

    /// <summary>Called when the token endpoint returns a new refresh token for the service account.</summary>
    ValueTask OnRefreshTokenRotatedAsync(string refreshToken, CancellationToken ct);
}

/// <summary>Credentials from configuration; rotated refresh tokens are kept in memory.</summary>
internal sealed class OptionsGraphCredentialSource(IOptions<GraphOptions> options) : IGraphCredentialSource
{
    private volatile string? _rotatedRefreshToken;

    public ValueTask<GraphCredentials> GetCredentialsAsync(CancellationToken ct)
    {
        var o = options.Value;
        return ValueTask.FromResult(new GraphCredentials(o.TenantId, o.ClientId, o.ClientSecret, o.ServiceAccount,
            _rotatedRefreshToken ?? o.ServiceAccountRefreshToken));
    }

    public ValueTask OnRefreshTokenRotatedAsync(string refreshToken, CancellationToken ct)
    {
        _rotatedRefreshToken = refreshToken;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Acquires and caches Microsoft Entra access tokens: client credentials (app-only) and the refresh-token flow for the
/// delegated service account. Tokens are cached until two minutes before expiry; acquisition is serialised per identity.
/// </summary>
public sealed class GraphTokenProvider
{
    public const string HttpClientName = "Microsoft365.Auth";
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IGraphCredentialSource _credentials;
    private readonly IOptions<GraphOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GraphTokenProvider> _logger;
    private readonly SemaphoreSlim _appGate = new(1, 1);
    private readonly SemaphoreSlim _delegatedGate = new(1, 1);
    private CachedToken? _appToken;
    private CachedToken? _delegatedToken;

    public GraphTokenProvider(IHttpClientFactory httpFactory, IGraphCredentialSource credentials, IOptions<GraphOptions> options,
        TimeProvider time, ILogger<GraphTokenProvider> logger)
    {
        _httpFactory = httpFactory;
        _credentials = credentials;
        _options = options;
        _time = time;
        _logger = logger;
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset RefreshAt);

    /// <summary>Returns a valid access token for the identity, acquiring one when the cache is empty or near expiry.</summary>
    public async Task<string> GetAccessTokenAsync(GraphAuth auth, CancellationToken ct)
    {
        var cached = ReadCache(auth);
        if (cached is not null && cached.RefreshAt > _time.GetUtcNow()) return cached.AccessToken;

        var gate = auth == GraphAuth.Application ? _appGate : _delegatedGate;
        await gate.WaitAsync(ct);
        try
        {
            cached = ReadCache(auth);
            if (cached is not null && cached.RefreshAt > _time.GetUtcNow()) return cached.AccessToken;
            var fresh = await AcquireAsync(auth, ct);
            if (auth == GraphAuth.Application) Volatile.Write(ref _appToken, fresh);
            else Volatile.Write(ref _delegatedToken, fresh);
            return fresh.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private CachedToken? ReadCache(GraphAuth auth) =>
        auth == GraphAuth.Application ? Volatile.Read(ref _appToken) : Volatile.Read(ref _delegatedToken);

    /// <summary>Drops the cached token when the API rejected it (only if it is still the cached one).</summary>
    public void Invalidate(GraphAuth auth, string accessToken)
    {
        if (auth == GraphAuth.Application)
        {
            var current = Volatile.Read(ref _appToken);
            if (current?.AccessToken == accessToken) Interlocked.CompareExchange(ref _appToken, null, current);
        }
        else
        {
            var current = Volatile.Read(ref _delegatedToken);
            if (current?.AccessToken == accessToken) Interlocked.CompareExchange(ref _delegatedToken, null, current);
        }
    }

    private async Task<CachedToken> AcquireAsync(GraphAuth auth, CancellationToken ct)
    {
        var options = _options.Value;
        var credentials = await _credentials.GetCredentialsAsync(ct);
        if (string.IsNullOrWhiteSpace(credentials.TenantId) || string.IsNullOrWhiteSpace(credentials.ClientId))
            throw new IntegrationException(IntegrationErrorKind.Authentication,
                "Microsoft 365 is not configured: Microsoft365:TenantId and Microsoft365:ClientId are required.");

        var form = new List<KeyValuePair<string, string>> { new("client_id", credentials.ClientId) };
        if (auth == GraphAuth.Application)
        {
            if (string.IsNullOrWhiteSpace(credentials.ClientSecret))
                throw new IntegrationException(IntegrationErrorKind.Authentication,
                    "Microsoft 365 is not configured: Microsoft365:ClientSecret is required for app-only access.");
            form.Add(new("grant_type", "client_credentials"));
            form.Add(new("client_secret", credentials.ClientSecret));
            form.Add(new("scope", GraphOptions.GraphScope));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
                throw new IntegrationException(IntegrationErrorKind.Authentication,
                    "The delegated Teams service account is not connected: Microsoft365:ServiceAccountRefreshToken is required to post Teams messages.");
            form.Add(new("grant_type", "refresh_token"));
            form.Add(new("refresh_token", credentials.RefreshToken));
            form.Add(new("scope", GraphOptions.GraphScope + " offline_access"));
            if (!string.IsNullOrWhiteSpace(credentials.ClientSecret)) form.Add(new("client_secret", credentials.ClientSecret));
        }

        var endpoint = new Uri($"{options.ResolvedAuthorityHost}/{Uri.EscapeDataString(credentials.TenantId)}/oauth2/v2.0/token");
        var http = _httpFactory.CreateClient(HttpClientName);
        var operation = $"Token request ({(auth == GraphAuth.Application ? "client credentials" : "service account refresh token")})";

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, timeout.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                if (attempt < options.MaxRetries) { await DelayAsync(GraphErrors.Backoff(attempt, options), ct); continue; }
                throw new IntegrationException(IntegrationErrorKind.Transient, $"{operation} timed out.", null, null, ex);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < options.MaxRetries) { await DelayAsync(GraphErrors.Backoff(attempt, options), ct); continue; }
                throw new IntegrationException(IntegrationErrorKind.Transient, $"{operation} failed: {ex.Message}", null, null, ex);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<TokenEndpointResponse>(GraphJson.Options, ct);
                    if (body?.AccessToken is not { Length: > 0 } accessToken)
                        throw new IntegrationException(IntegrationErrorKind.Authentication, $"{operation} returned no access token.", status);
                    if (auth == GraphAuth.ServiceAccount && !string.IsNullOrEmpty(body.RefreshToken) && body.RefreshToken != credentials.RefreshToken)
                        await _credentials.OnRefreshTokenRotatedAsync(body.RefreshToken, ct);
                    var lifetime = TimeSpan.FromSeconds(Math.Max(60, body.ExpiresIn ?? 3600));
                    var refreshIn = lifetime > ExpirySkew * 2 ? lifetime - ExpirySkew : lifetime / 2;
                    _logger.LogDebug("Acquired {Identity} Graph token valid for {Lifetime}", auth, lifetime);
                    return new CachedToken(accessToken, _time.GetUtcNow() + refreshIn);
                }

                var retryAfter = GraphErrors.ReadRetryAfter(response.Headers, _time.GetUtcNow());
                if (GraphErrors.IsRetryable(status) && attempt < options.MaxRetries)
                {
                    await DelayAsync(retryAfter is { } ra ? GraphErrors.Cap(ra, options) : GraphErrors.Backoff(attempt, options), ct);
                    continue;
                }

                var (code, message) = await GraphErrors.ReadErrorAsync(response, ct);
                // Any 4xx from the token endpoint (invalid_client, invalid_grant, unauthorized_client…) is an authentication failure.
                var kind = status is >= 400 and < 500 and not 429 ? IntegrationErrorKind.Authentication : GraphErrors.KindFor(status, code);
                throw new IntegrationException(kind,
                    $"{operation} failed with HTTP {status}{(code is null ? "" : " " + code)}: {message ?? "(no error message)"}",
                    status, retryAfter);
            }
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, _time, ct);
}
