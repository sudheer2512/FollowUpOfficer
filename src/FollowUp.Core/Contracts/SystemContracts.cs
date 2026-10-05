namespace FollowUp.Core.Contracts;

// Small response shapes used by the HTTP API (docs/API.md).

/// <summary>GET /api/auth/config.</summary>
public sealed record AuthConfigDto(bool EntraEnabled, string? TenantId, string? ClientId, string? ApiScope, bool DemoMode, List<DemoUserDto> DemoUsers);

public sealed record DemoUserDto(string UserName, string Name, string? Title, string Role);

/// <summary>POST /api/auth/login body.</summary>
public sealed record LoginRequest(string UserName, string Password);

/// <summary>GET /api/health (anonymous).</summary>
public sealed record HealthDto(string Status, string Database, string Mode, bool JobsEnabled, DateTime ServerTime);

/// <summary>GET /api/system/info.</summary>
public sealed record SystemInfoDto(string Organisation, string TimeZone, string Mode, string AiProvider, List<HostHeartbeat> Hosts, string Version);

public sealed record QueuedResult(int Queued);

public sealed record CountResult(int Count);

public sealed record RetryResult(int Retried);

public sealed record AuditVerifyResult(bool Ok, Guid? BrokenEntryId);

public sealed record ReasonRequest(string? Reason);

public sealed record EnabledRequest(bool Enabled);

public sealed record ResetCheckpointRequest(bool FromScratch);

public sealed record IdsRequest(List<Guid> Ids);
