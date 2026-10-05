namespace FollowUp.AI.Llm;

/// <summary>
/// Consecutive-failure circuit breaker: after <c>failureThreshold</c> consecutive failures the LLM is skipped for
/// <c>openDuration</c>. When that elapses one trial call is allowed (half-open); a failure re-opens the circuit at once,
/// a success closes it. Thread-safe.
/// </summary>
public sealed class LlmCircuitBreaker
{
    private readonly object _gate = new();
    private readonly int _threshold;
    private readonly TimeSpan _openDuration;
    private readonly TimeProvider _time;
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;

    /// <summary>Creates a breaker.</summary>
    public LlmCircuitBreaker(int failureThreshold, TimeSpan openDuration, TimeProvider? timeProvider = null)
    {
        _threshold = Math.Max(1, failureThreshold);
        _openDuration = openDuration < TimeSpan.Zero ? TimeSpan.Zero : openDuration;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True while calls are being skipped.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate) return _openUntil is { } until && _time.GetUtcNow() < until;
        }
    }

    /// <summary>Failures since the last success.</summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_gate) return _consecutiveFailures;
        }
    }

    /// <summary>When the circuit closes again (null when closed).</summary>
    public DateTimeOffset? OpenUntil
    {
        get
        {
            lock (_gate) return _openUntil is { } until && _time.GetUtcNow() < until ? until : null;
        }
    }

    /// <summary>False while the circuit is open.</summary>
    public bool AllowRequest() => !IsOpen;

    /// <summary>Records a valid answer: closes the circuit.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
        }
    }

    /// <summary>Records a failure; returns true when this failure opened (or re-opened) the circuit.</summary>
    public bool RecordFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures < _threshold) return false;
            _openUntil = _time.GetUtcNow() + _openDuration;
            return true;
        }
    }
}
