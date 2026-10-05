namespace FollowUp.AI;

/// <summary>
/// Configuration section "Ai". The deterministic heuristic engine always runs; an LLM provider, when configured,
/// refines its results and any LLM failure falls back to the heuristic result.
/// </summary>
/// <example>
/// <code>
/// "Ai": { "Provider": "Anthropic", "Model": "claude-opus-5-5", "Effort": "low", "TimeoutSeconds": 30 }
/// </code>
/// </example>
public sealed class AiOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ai";

    /// <summary>Provider value: heuristic engine only (default).</summary>
    public const string HeuristicProvider = "Heuristic";

    /// <summary>Provider value: Anthropic Claude via the official SDK.</summary>
    public const string AnthropicProvider = "Anthropic";

    /// <summary>Provider value: any OpenAI-compatible chat-completions endpoint (Azure OpenAI, vLLM, Ollama, LM Studio…).</summary>
    public const string OpenAiCompatibleProvider = "OpenAICompatible";

    /// <summary>Model used for Anthropic when <see cref="Model"/> is not set.</summary>
    public const string DefaultAnthropicModel = "claude-opus-5-5";

    /// <summary>"Heuristic" (default) | "Anthropic" | "OpenAICompatible".</summary>
    public string Provider { get; set; } = HeuristicProvider;

    /// <summary>Model id. Anthropic defaults to <see cref="DefaultAnthropicModel"/>; OpenAI-compatible requires it.</summary>
    public string? Model { get; set; }

    /// <summary>API key. Falls back to ANTHROPIC_API_KEY (Anthropic) or OPENAI_API_KEY (OpenAI-compatible).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Endpoint base URL: required for OpenAI-compatible (e.g. "https://my.openai.azure.com/openai/v1"); optional override for Anthropic.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Anthropic effort: "low" (default — high-volume classification and extraction), "medium", "high", "xhigh", "max".</summary>
    public string Effort { get; set; } = "low";

    /// <summary>Per-call timeout; on expiry the heuristic result is used. Default 30.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Consecutive LLM failures that open the circuit breaker. Default 3.</summary>
    public int CircuitBreakerFailures { get; set; } = 3;

    /// <summary>How long the LLM is skipped once the circuit opens. Default 10.</summary>
    public int CircuitBreakerMinutes { get; set; } = 10;

    /// <summary>
    /// Task matching is sent to the LLM only when the best heuristic candidate scores at least this much — a message
    /// with no plausible candidate is not worth a call. Default 0.2.
    /// </summary>
    public double MinMatchConfidenceForLlm { get; set; } = 0.2;

    /// <summary>Output token cap for structured calls. Default 2048.</summary>
    public int MaxOutputTokens { get; set; } = 2048;
}
