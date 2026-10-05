using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FollowUp.AI.Llm;

/// <summary>
/// Guards every LLM call made by the AI services: skips the call when no provider is configured or the circuit is
/// open, applies the per-call timeout, converts every failure (exception, timeout, refusal, invalid or out-of-range
/// answer) into "no result" so the caller keeps its heuristic result, and feeds the circuit breaker. A caller-requested
/// cancellation is propagated as <see cref="OperationCanceledException"/>. Thread-safe; one instance per application.
/// </summary>
public sealed class LlmGateway
{
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;

    /// <summary>Creates the gateway for a provider.</summary>
    public LlmGateway(ILlmClient client, IOptions<AiOptions> options, TimeProvider? timeProvider = null, ILogger<LlmGateway>? logger = null)
    {
        var o = options.Value;
        Client = client;
        Options = o;
        _timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 600));
        Breaker = new LlmCircuitBreaker(o.CircuitBreakerFailures, TimeSpan.FromMinutes(Math.Max(0, o.CircuitBreakerMinutes)), timeProvider);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The provider.</summary>
    public ILlmClient Client { get; }

    /// <summary>The AI options in force.</summary>
    public AiOptions Options { get; }

    /// <summary>The circuit breaker shared by all AI services.</summary>
    public LlmCircuitBreaker Breaker { get; }

    /// <summary>A provider is configured.</summary>
    public bool IsEnabled => Client.IsConfigured;

    /// <summary>A provider is configured and the circuit is closed.</summary>
    public bool IsAvailable => IsEnabled && !Breaker.IsOpen;

    /// <summary>"Heuristic" without a provider, otherwise the provider's name, e.g. "Anthropic (claude-opus-5-5)".</summary>
    public string ProviderName => IsEnabled ? Client.Name : "Heuristic";

    /// <summary>
    /// Runs <paramref name="request"/> and validates the answer with <paramref name="parse"/> (which returns null — or
    /// throws <see cref="LlmException"/> — for an unusable answer). Returns null on any failure.
    /// </summary>
    public async Task<T?> TryCompleteAsync<T>(LlmJsonRequest request, Func<JsonElement, T?> parse, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        if (!IsEnabled || !Breaker.AllowRequest()) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        JsonElement json;
        try
        {
            json = await Client.CompleteJsonAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException e)
        {
            Failed(request, LlmFailureKind.Timeout, $"no answer within {_timeout.TotalSeconds:0}s", e);
            return null;
        }
        catch (LlmException e)
        {
            Failed(request, e.Kind, e.Message, e);
            return null;
        }
        catch (Exception e)
        {
            Failed(request, LlmFailureKind.Unknown, e.Message, e);
            return null;
        }

        T? result;
        try
        {
            result = parse(json);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Failed(request, LlmFailureKind.InvalidResponse, e.Message, e);
            return null;
        }
        if (result is null)
        {
            Failed(request, LlmFailureKind.InvalidResponse, "the answer failed validation", null);
            return null;
        }
        Breaker.RecordSuccess();
        return result;
    }

    private void Failed(LlmJsonRequest request, LlmFailureKind kind, string message, Exception? e)
    {
        var opened = Breaker.RecordFailure();
        _logger.LogWarning(e, "LLM {Provider} failed for {Purpose} ({Kind}): {Message}. Using the heuristic result.", Client.Name, request.Purpose, kind, message);
        if (opened)
            _logger.LogWarning("LLM {Provider} disabled until {Until:u} after {Failures} consecutive failures; the heuristic engine is used meanwhile.",
                Client.Name, Breaker.OpenUntil, Breaker.ConsecutiveFailures);
    }
}
