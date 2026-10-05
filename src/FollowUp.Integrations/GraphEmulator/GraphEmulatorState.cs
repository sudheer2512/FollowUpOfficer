using System.Collections.Concurrent;
using FollowUp.Core.Abstractions;

namespace FollowUp.Integrations.GraphEmulator;

/// <summary>Validated bearer token of an emulator request.</summary>
internal sealed record EmulatorPrincipal(string Kind, string ClientId, string? UserId, DateTime ExpiresAt)
{
    public bool IsDelegated => Kind == "delegated";
}

/// <summary>
/// Process-wide emulator state: the write lock (all store writes are serialised, which keeps the change sequence gap-free
/// for delta readers), a short-lived fault-settings cache and a cache of validated tokens.
/// </summary>
internal sealed class GraphEmulatorState
{
    private static readonly TimeSpan FaultCacheTtl = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private TenantFaults? _faults;
    private DateTime _faultsLoadedAt;

    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    public ConcurrentDictionary<string, EmulatorPrincipal> Tokens { get; } = new(StringComparer.Ordinal);

    public TenantFaults? CachedFaults(DateTime nowUtc)
    {
        lock (_gate) return _faults is not null && nowUtc - _faultsLoadedAt < FaultCacheTtl ? _faults : null;
    }

    public void CacheFaults(TenantFaults faults, DateTime nowUtc)
    {
        lock (_gate)
        {
            _faults = faults;
            _faultsLoadedAt = nowUtc;
        }
    }

    public void ForgetTokens() => Tokens.Clear();
}
