using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FollowUp.AI.Llm;

/// <summary>
/// <see cref="ILlmClient"/> for organisations that run Azure OpenAI or a local / self-hosted model behind an
/// OpenAI-compatible API: POST {BaseUrl}/chat/completions with <c>response_format</c> = strict <c>json_schema</c>.
/// HTTP errors, refusals, truncation and invalid JSON become <see cref="LlmException"/>. Thread-safe.
/// </summary>
public sealed partial class OpenAiCompatibleLlmClient : ILlmClient
{
    /// <summary>Name of the <see cref="IHttpClientFactory"/> client registered by <c>AddFollowUpAI</c>.</summary>
    public const string HttpClientName = "FollowUp.AI.OpenAICompatible";

    private readonly Func<HttpClient> _http;
    private readonly string? _baseUrl;
    private readonly string? _model;
    private readonly string? _apiKey;
    private readonly ILogger _logger;

    /// <summary>Creates the client using a named client from <paramref name="factory"/>.</summary>
    public OpenAiCompatibleLlmClient(IHttpClientFactory factory, IOptions<AiOptions> options, ILogger<OpenAiCompatibleLlmClient>? logger = null)
        : this(() => factory.CreateClient(HttpClientName), options.Value, logger)
    {
    }

    /// <summary>Creates the client over a caller-owned <see cref="HttpClient"/> (tests, console tools).</summary>
    public OpenAiCompatibleLlmClient(HttpClient httpClient, AiOptions options, ILogger<OpenAiCompatibleLlmClient>? logger = null)
        : this(() => httpClient, options, logger)
    {
    }

    private OpenAiCompatibleLlmClient(Func<HttpClient> http, AiOptions o, ILogger? logger)
    {
        _http = http;
        _logger = logger ?? NullLogger.Instance;
        _baseUrl = string.IsNullOrWhiteSpace(o.BaseUrl) ? null : o.BaseUrl.Trim().TrimEnd('/');
        _model = string.IsNullOrWhiteSpace(o.Model) ? null : o.Model.Trim();
        _apiKey = !string.IsNullOrWhiteSpace(o.ApiKey) ? o.ApiKey : Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    }

    /// <inheritdoc />
    public string Name => $"OpenAI-compatible ({_model ?? "no model"})";

    /// <inheritdoc />
    public bool IsConfigured => _baseUrl is not null && _model is not null;

    /// <inheritdoc />
    public async Task<JsonElement> CompleteJsonAsync(LlmJsonRequest request, CancellationToken ct)
    {
        if (!IsConfigured) throw new LlmException(LlmFailureKind.NotConfigured, "OpenAI-compatible provider needs Ai:BaseUrl and Ai:Model.");

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = Math.Max(256, request.MaxTokens),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = SchemaNameRegex().Replace(request.Purpose, "_"),
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(request.JsonSchema.GetRawText()),
                },
            },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            if (Uri.TryCreate(_baseUrl, UriKind.Absolute, out var u) && u.Host.EndsWith(".azure.com", StringComparison.OrdinalIgnoreCase))
                message.Headers.Add("api-key", _apiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http().SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e) { throw Fail(LlmFailureKind.Network, "network error", e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw Fail(LlmFailureKind.Timeout, "HTTP timeout", e); }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var kind = response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => LlmFailureKind.RateLimited,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => LlmFailureKind.Unauthorized,
                    HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity => LlmFailureKind.BadRequest,
                    >= HttpStatusCode.InternalServerError => LlmFailureKind.ServerError,
                    _ => LlmFailureKind.Unknown,
                };
                throw new LlmException(kind, $"{Name} returned HTTP {(int)response.StatusCode}.");
            }

            JsonElement choice;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    throw new LlmException(LlmFailureKind.InvalidResponse, $"{Name} returned no choices.");
                choice = choices[0].Clone();
            }
            catch (JsonException e) { throw Fail(LlmFailureKind.InvalidResponse, "invalid response envelope", e); }

            var finish = LlmJson.GetString(choice, "finish_reason");
            var msg = choice.TryGetProperty("message", out var m) ? m : default;
            if (LlmJson.GetText(msg, "refusal") is not null || finish == "content_filter")
                throw new LlmException(LlmFailureKind.Refusal, $"{Name} declined the {request.Purpose} request.");
            if (finish == "length")
                throw new LlmException(LlmFailureKind.InvalidResponse, $"{Name} answer for {request.Purpose} was truncated.");
            return LlmJson.ParseObject(LlmJson.GetString(msg, "content"), Name);
        }
    }

    private LlmException Fail(LlmFailureKind kind, string what, Exception e)
    {
        _logger.LogDebug(e, "OpenAI-compatible call failed: {What}", what);
        return new LlmException(kind, $"{Name} {what}: {e.Message}", e);
    }

    [GeneratedRegex(@"[^A-Za-z0-9_-]", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNameRegex();
}
