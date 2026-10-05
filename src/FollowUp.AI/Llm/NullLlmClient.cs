using System.Text.Json;

namespace FollowUp.AI.Llm;

/// <summary>The "no LLM" provider: never configured, so every service uses its heuristic engine only.</summary>
public sealed class NullLlmClient : ILlmClient
{
    /// <summary>Shared instance.</summary>
    public static NullLlmClient Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "None";

    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public Task<JsonElement> CompleteJsonAsync(LlmJsonRequest request, CancellationToken ct) =>
        throw new LlmException(LlmFailureKind.NotConfigured, "No LLM provider is configured.");
}
