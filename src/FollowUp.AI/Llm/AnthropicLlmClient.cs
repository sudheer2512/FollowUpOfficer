using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FollowUp.AI.Llm;

/// <summary>
/// <see cref="ILlmClient"/> backed by the official Anthropic C# SDK, using structured outputs
/// (<c>output_config.format</c> = JSON schema) and the configured effort. Thinking is left at the model default
/// (Claude Opus 5.5 does not accept disabled thinking or a thinking budget). A refusal stop reason, a truncated answer
/// or any SDK exception becomes an <see cref="LlmException"/> so the caller falls back to the heuristic engine.
/// The SDK itself retries 429 / 5xx / connection errors twice. Thread-safe; register as a singleton.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    private readonly AnthropicClient? _client;
    private readonly string _model;
    private readonly string _effort;
    private readonly ILogger _logger;

    /// <summary>Creates the client from options; without an API key (option or ANTHROPIC_API_KEY) it reports not configured.</summary>
    public AnthropicLlmClient(IOptions<AiOptions> options, ILogger<AnthropicLlmClient>? logger = null)
    {
        var o = options.Value;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _model = string.IsNullOrWhiteSpace(o.Model) ? AiOptions.DefaultAnthropicModel : o.Model.Trim();
        _effort = string.IsNullOrWhiteSpace(o.Effort) ? "low" : o.Effort.Trim().ToLowerInvariant();
        var key = !string.IsNullOrWhiteSpace(o.ApiKey) ? o.ApiKey : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;
        _client = string.IsNullOrWhiteSpace(o.BaseUrl)
            ? new AnthropicClient { ApiKey = key, Timeout = TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds)) }
            : new AnthropicClient { ApiKey = key, BaseUrl = o.BaseUrl.Trim(), Timeout = TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds)) };
    }

    /// <inheritdoc />
    public string Name => $"Anthropic ({_model})";

    /// <inheritdoc />
    public bool IsConfigured => _client is not null;

    /// <inheritdoc />
    public async Task<JsonElement> CompleteJsonAsync(LlmJsonRequest request, CancellationToken ct)
    {
        if (_client is null) throw new LlmException(LlmFailureKind.NotConfigured, "Anthropic API key is not configured.");

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = Math.Max(256, request.MaxTokens),
            System = request.SystemPrompt,
            Messages = [new() { Role = Role.User, Content = request.UserPrompt }],
            OutputConfig = new OutputConfig
            {
                Effort = ParseEffort(_effort),
                Format = new JsonOutputFormat { Schema = ToSchema(request.JsonSchema) },
            },
        };

        Message message;
        try
        {
            message = await _client.Messages.Create(parameters, ct).ConfigureAwait(false);
        }
        catch (AnthropicRateLimitException e) { throw Fail(LlmFailureKind.RateLimited, "rate limited (429)", e); }
        catch (Anthropic5xxException e) { throw Fail(LlmFailureKind.ServerError, "server error (5xx)", e); }
        catch (AnthropicIOException e) { throw Fail(LlmFailureKind.Network, "network error", e); }
        catch (AnthropicUnauthorizedException e) { throw Fail(LlmFailureKind.Unauthorized, "unauthorized (401) — check the API key", e); }
        catch (AnthropicForbiddenException e) { throw Fail(LlmFailureKind.Unauthorized, "forbidden (403)", e); }
        catch (AnthropicNotFoundException e) { throw Fail(LlmFailureKind.BadRequest, $"not found (404) — check the model name '{_model}'", e); }
        catch (AnthropicBadRequestException e) { throw Fail(LlmFailureKind.BadRequest, "bad request (400)", e); }
        catch (AnthropicUnprocessableEntityException e) { throw Fail(LlmFailureKind.BadRequest, "unprocessable request (422)", e); }
        catch (AnthropicApiException e) { throw Fail(LlmFailureKind.Unknown, "API error", e); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is not LlmException) { throw Fail(LlmFailureKind.Unknown, "unexpected SDK error", e); }

        // A refusal is a normal HTTP 200 response: check it before reading the content.
        if (message.StopReason == "refusal")
            throw new LlmException(LlmFailureKind.Refusal, $"Anthropic declined the {request.Purpose} request (refusal).");
        if (message.StopReason == "max_tokens")
            throw new LlmException(LlmFailureKind.InvalidResponse, $"Anthropic answer for {request.Purpose} was truncated at max_tokens.");

        var text = string.Concat(message.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return LlmJson.ParseObject(text, Name);
    }

    private LlmException Fail(LlmFailureKind kind, string what, Exception e)
    {
        _logger.LogDebug(e, "Anthropic call failed: {What}", what);
        return new LlmException(kind, $"Anthropic {what}: {e.Message}", e);
    }

    private static Effort ParseEffort(string effort) => effort switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Low,
    };

    private static Dictionary<string, JsonElement> ToSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            throw new LlmException(LlmFailureKind.BadRequest, "The JSON schema must be an object.");
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in schema.EnumerateObject()) dict[p.Name] = p.Value.Clone();
        return dict;
    }
}
