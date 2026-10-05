namespace FollowUp.Domain;

/// <summary>
/// Durable synchronization checkpoint per monitored source (spec §60, §64). The sync runner advances it in
/// the same transaction as the imported messages, so a restart resumes exactly after the last committed page.
/// </summary>
public sealed class SynchronizationCheckpoint : Entity
{
    public IntegrationType IntegrationType { get; set; }
    /// <summary>Mailbox + folder ("ceo@contoso.com/inbox"), chat id, or "teamId/channelId".</summary>
    public string SourceKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Owner of the monitored mailbox / chat — drives visibility of unlinked messages.</summary>
    public Guid? OwnerEmployeeId { get; set; }
    public bool Enabled { get; set; } = true;
    public string? LastProcessedExternalId { get; set; }
    /// <summary>Timestamp watermark of the newest processed item (used where Graph has no delta query).</summary>
    public DateTime? LastProcessedAt { get; set; }
    public string? DeltaToken { get; set; }
    /// <summary>Mid-pagination continuation (nextLink) — lets a crashed sync resume inside a delta round.</summary>
    public string? ContinuationToken { get; set; }
    public DateTime? LastSuccessfulSyncAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public SyncState Status { get; set; } = SyncState.Idle;
    public string? Error { get; set; }
    public int RetryCount { get; set; }
    public long ItemsImportedTotal { get; set; }
    public int LastRunItemCount { get; set; }
    public int Version { get; set; }
}

/// <summary>Registered background job with its durable schedule and lease (spec §65).</summary>
public sealed class ScheduledJob : Entity
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JobScheduleKind ScheduleKind { get; set; }
    public int IntervalSeconds { get; set; }
    public TimeOnly? TimeOfDay { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public MisfirePolicy Misfire { get; set; }
    public int MisfireWindowMinutes { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxRetries { get; set; } = 5;
    public bool Enabled { get; set; } = true;
    public JobState State { get; set; } = JobState.Idle;
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastStartedAt { get; set; }
    public DateTime? LastSucceededAt { get; set; }
    public DateTime? LastFailedAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    /// <summary>Job-level checkpoint (e.g. the boundary processed by the last reminder run).</summary>
    public string? Checkpoint { get; set; }
    public bool RunRequested { get; set; }
    public int Version { get; set; }
}

/// <summary>Execution history for a job run (spec §65).</summary>
public sealed class BackgroundJobExecution : Entity
{
    public string JobKey { get; set; } = "";
    public JobTrigger Trigger { get; set; }
    public string Host { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public JobOutcome Outcome { get; set; }
    public int ItemsProcessed { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public int Attempt { get; set; }
    /// <summary>When the run was due — the gap to StartedAt shows downtime catch-up.</summary>
    public DateTime? DueAt { get; set; }
}

/// <summary>Failed Microsoft Graph / AI / delivery call (spec §67).</summary>
public sealed class IntegrationError : Entity
{
    public string Integration { get; set; } = "";
    public string? SourceKey { get; set; }
    public string Operation { get; set; } = "";
    public IntegrationErrorKind Kind { get; set; }
    public int? StatusCode { get; set; }
    public string Message { get; set; } = "";
    public int? RetryAfterSeconds { get; set; }
    public DateTime OccurredAt { get; set; }
    public bool Resolved { get; set; }
}

/// <summary>Immutable audit trail (spec §68). Rows are append-only; each row chains the previous row's hash.</summary>
public sealed class AuditLog : Entity
{
    public DateTime At { get; set; }
    public Guid? ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public Guid? EntityId { get; set; }
    public Guid? TaskId { get; set; }
    public string? Field { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Details { get; set; }
    public ChangeSource Source { get; set; }
    public string? PrevHash { get; set; }
    public string Hash { get; set; } = "";
}

/// <summary>Saved task-list filter (spec §53).</summary>
public sealed class SavedView : Entity
{
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string FilterJson { get; set; } = "{}";
    public bool IsShared { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Key/value system setting stored as JSON (follow-up, digest, AI and integration options).</summary>
public sealed class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }
}

/// <summary>Per-year counter used to allocate TASK-yyyy-nnnnn numbers inside the creating transaction.</summary>
public sealed class NumberSequence
{
    public string Key { get; set; } = "";
    public int NextValue { get; set; }
}

/// <summary>
/// Real-time event outbox. Workers and the API append rows; the API tails them (monotonic Id cursor) and
/// broadcasts over SignalR. A bigint key is used deliberately: it is a cursor, not an entity identity.
/// </summary>
public sealed class UiEvent
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Topic { get; set; } = "";
    public Guid? TaskId { get; set; }
    public Guid? AudienceId { get; set; }
    public string? Payload { get; set; }
}

/// <summary>Microsoft Graph credential / connection (spec §69 secure storage). Secrets are stored encrypted.</summary>
public sealed class IntegrationAccount : Entity
{
    public string Name { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ProtectedClientSecret { get; set; }
    /// <summary>Delegated service account refresh token (Teams chat/channel posting needs delegated permissions).</summary>
    public string? ProtectedRefreshToken { get; set; }
    public string? ServiceAccountUpn { get; set; }
    public string? ServiceAccountUserId { get; set; }
    public string Scopes { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime? LastValidatedAt { get; set; }
    public string? LastError { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Outlook calendar event pushed for an important deadline / review / approval (spec §72).</summary>
public sealed class CalendarEntry : Entity
{
    public Guid TaskId { get; set; }
    public string Kind { get; set; } = "";
    public Guid OwnerEmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public string? ExternalEventId { get; set; }
    public string Fingerprint { get; set; } = "";
    public DateTime? SyncedAt { get; set; }
    public bool Deleted { get; set; }
    public string? Error { get; set; }
}
