using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations.Graph;

/// <summary>
/// Minimal Microsoft Graph REST client over <see cref="IHttpClientFactory"/>: bearer tokens, retries for 429/503/504,
/// timeouts and network failures (Retry-After honoured and capped, otherwise exponential backoff with jitter), a single
/// token refresh on 401, failure classification into <see cref="IntegrationException"/> and @odata.nextLink paging.
/// </summary>
public sealed class GraphClient
{
    public const string HttpClientName = "Microsoft365.Graph";

    private readonly IHttpClientFactory _httpFactory;
    private readonly GraphTokenProvider _tokens;
    private readonly IOptions<GraphOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GraphClient> _logger;

    public GraphClient(IHttpClientFactory httpFactory, GraphTokenProvider tokens, IOptions<GraphOptions> options, TimeProvider time,
        ILogger<GraphClient> logger)
    {
        _httpFactory = httpFactory;
        _tokens = tokens;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Graph base URL in use (e.g. https://graph.microsoft.com/v1.0).</summary>
    public string BaseUrl => _options.Value.ResolvedGraphBaseUrl;

    /// <summary>GET a resource and deserialise it.</summary>
    public async Task<T> GetAsync<T>(string pathOrUrl, GraphAuth auth, CancellationToken ct,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        using var response = await SendCoreAsync(HttpMethod.Get, pathOrUrl, null, auth, headers, ct);
        return await ReadAsync<T>(response, HttpMethod.Get, ct)
               ?? throw new IntegrationException(IntegrationErrorKind.Unknown, $"Graph GET {pathOrUrl} returned an empty body.");
    }

    /// <summary>Sends a JSON body and deserialises the response (null for 202 / 204).</summary>
    public async Task<T?> SendAsync<T>(HttpMethod method, string pathOrUrl, JsonNode? body, GraphAuth auth, CancellationToken ct,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        using var response = await SendCoreAsync(method, pathOrUrl, body, auth, headers, ct);
        return await ReadAsync<T>(response, method, ct);
    }

    /// <summary>Sends a request whose response body is not needed.</summary>
    public async Task SendAsync(HttpMethod method, string pathOrUrl, JsonNode? body, GraphAuth auth, CancellationToken ct,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        using var response = await SendCoreAsync(method, pathOrUrl, body, auth, headers, ct);
    }

    /// <summary>One page of a collection (value + nextLink / deltaLink).</summary>
    internal Task<GraphCollection<T>> GetPageAsync<T>(string pathOrUrl, GraphAuth auth, CancellationToken ct,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null) =>
        GetAsync<GraphCollection<T>>(pathOrUrl, auth, ct, headers);

    /// <summary>Follows @odata.nextLink until the collection ends or <paramref name="maxItems"/> is reached.</summary>
    internal async Task<List<T>> GetAllAsync<T>(string pathOrUrl, GraphAuth auth, int maxItems, CancellationToken ct,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        var items = new List<T>();
        string? next = pathOrUrl;
        while (next is not null && items.Count < maxItems)
        {
            var page = await GetPageAsync<T>(next, auth, ct, headers);
            items.AddRange(page.Value);
            next = page.NextLink;
        }
        if (items.Count > maxItems) items.RemoveRange(maxItems, items.Count - maxItems);
        return items;
    }

    private async Task<T?> ReadAsync<T>(HttpResponseMessage response, HttpMethod method, CancellationToken ct)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.Accepted) return default;
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(text, GraphJson.Options);
        }
        catch (JsonException ex)
        {
            throw new IntegrationException(IntegrationErrorKind.Unknown,
                $"Graph {method} {GraphErrors.Describe(response.RequestMessage!.RequestUri!)} returned an unreadable body: {ex.Message}",
                (int)response.StatusCode, null, ex);
        }
    }

    /// <summary>Resolves a relative Graph path or an absolute link; absolute links must stay on the Graph origin (tokens never leave it).</summary>
    internal Uri ResolveUri(string pathOrUrl)
    {
        var baseUrl = _options.Value.ResolvedGraphBaseUrl;
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var absolute = new Uri(pathOrUrl);
            var origin = new Uri(baseUrl);
            if (Uri.Compare(absolute, origin, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0)
                throw new IntegrationException(IntegrationErrorKind.Validation,
                    $"Refusing to follow a Graph link to a different origin ({absolute.GetLeftPart(UriPartial.Authority)}).");
            return absolute;
        }
        return new Uri(baseUrl + "/" + pathOrUrl.TrimStart('/'));
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string pathOrUrl, JsonNode? body, GraphAuth auth,
        IReadOnlyList<KeyValuePair<string, string>>? headers, CancellationToken ct)
    {
        var options = _options.Value;
        var uri = ResolveUri(pathOrUrl);
        var operation = $"Graph {method} {GraphErrors.Describe(uri)}";
        var payload = body?.ToJsonString(GraphJson.Options);
        var http = _httpFactory.CreateClient(HttpClientName);
        var tokenRefreshed = false;

        for (var attempt = 0; ; attempt++)
        {
            var token = await _tokens.GetAccessTokenAsync(auth, ct);
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (headers is not null)
                foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            if (payload is not null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                if (attempt < options.MaxRetries)
                {
                    _logger.LogWarning("{Operation} timed out (attempt {Attempt}); retrying", operation, attempt + 1);
                    await DelayAsync(GraphErrors.Backoff(attempt, options), ct);
                    continue;
                }
                throw new IntegrationException(IntegrationErrorKind.Transient,
                    $"{operation} timed out after {options.RequestTimeoutSeconds}s ({attempt + 1} attempts).", null, null, ex);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < options.MaxRetries)
                {
                    _logger.LogWarning(ex, "{Operation} failed with a network error (attempt {Attempt}); retrying", operation, attempt + 1);
                    await DelayAsync(GraphErrors.Backoff(attempt, options), ct);
                    continue;
                }
                throw new IntegrationException(IntegrationErrorKind.Transient,
                    $"{operation} failed with a network error after {attempt + 1} attempts: {ex.Message}", null, null, ex);
            }

            if (response.IsSuccessStatusCode) return response;

            var status = (int)response.StatusCode;
            if (status == 401 && !tokenRefreshed)
            {
                // The cached token may have been revoked or expired early: acquire a fresh one once.
                tokenRefreshed = true;
                _tokens.Invalidate(auth, token);
                response.Dispose();
                attempt--;
                continue;
            }

            var retryAfter = GraphErrors.ReadRetryAfter(response.Headers, _time.GetUtcNow());
            if (GraphErrors.IsRetryable(status) && attempt < options.MaxRetries)
            {
                var delay = retryAfter is { } ra ? GraphErrors.Cap(ra, options) : GraphErrors.Backoff(attempt, options);
                _logger.LogWarning("{Operation} returned {Status} (attempt {Attempt}); retrying in {Delay}", operation, status, attempt + 1, delay);
                response.Dispose();
                await DelayAsync(delay, ct);
                continue;
            }

            using (response)
            {
                var (code, message) = await GraphErrors.ReadErrorAsync(response, ct);
                throw GraphErrors.Classify(operation, status, code, message, retryAfter);
            }
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, _time, ct);
}
