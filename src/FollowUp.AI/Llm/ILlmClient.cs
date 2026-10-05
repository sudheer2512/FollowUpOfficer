using System.Text.Json;

namespace FollowUp.AI.Llm;

/// <summary>
/// Provider-independent LLM port: one structured (JSON-schema constrained) completion per call. Implementations throw
/// <see cref="LlmException"/> for every provider failure (refusal, rate limit, network, invalid output…) so callers can
/// fall back to the deterministic heuristic engine. Implementations must be thread-safe.
/// </summary>
public interface ILlmClient
{
    /// <summary>Display name recorded as the provider, e.g. "Anthropic (claude-opus-5-5)".</summary>
    string Name { get; }

    /// <summary>False when the provider lacks the settings it needs (API key, base URL…); callers then skip it.</summary>
    bool IsConfigured { get; }

    /// <summary>Runs one completion and returns the parsed JSON object that conforms to <see cref="LlmJsonRequest.JsonSchema"/>.</summary>
    /// <exception cref="LlmException">Any provider failure.</exception>
    Task<JsonElement> CompleteJsonAsync(LlmJsonRequest request, CancellationToken ct);
}

/// <summary>A structured completion request.</summary>
/// <param name="Purpose">Short machine name of the use ("classify", "match", "extract_commitments"…); used as the schema name and in logs.</param>
/// <param name="SystemPrompt">Instructions.</param>
/// <param name="UserPrompt">The data to work on.</param>
/// <param name="JsonSchema">JSON schema (an object schema) the answer must follow.</param>
/// <param name="MaxTokens">Output token cap.</param>
public sealed record LlmJsonRequest(string Purpose, string SystemPrompt, string UserPrompt, JsonElement JsonSchema, int MaxTokens);

/// <summary>Why an LLM call failed.</summary>
public enum LlmFailureKind
{
    /// <summary>The provider is not configured.</summary>
    NotConfigured,
    /// <summary>HTTP 429 after the SDK's own retries.</summary>
    RateLimited,
    /// <summary>HTTP 5xx after retries.</summary>
    ServerError,
    /// <summary>Connection / DNS / TLS failure.</summary>
    Network,
    /// <summary>The call exceeded the configured timeout.</summary>
    Timeout,
    /// <summary>HTTP 401 / 403.</summary>
    Unauthorized,
    /// <summary>HTTP 400 / 404 / 422 — the request or model name is wrong.</summary>
    BadRequest,
    /// <summary>The model declined to answer (stop reason "refusal").</summary>
    Refusal,
    /// <summary>The answer was truncated, empty, not JSON, or failed validation.</summary>
    InvalidResponse,
    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>A provider failure. Callers fall back to the heuristic result and never surface this to users.</summary>
public sealed class LlmException : Exception
{
    /// <summary>Creates a failure of the given kind.</summary>
    public LlmException(LlmFailureKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;

    /// <summary>The failure category.</summary>
    public LlmFailureKind Kind { get; }
}
