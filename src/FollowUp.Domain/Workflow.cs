namespace FollowUp.Domain;

/// <summary>Configurable escalation ladder (spec §34). Critical tasks typically use a faster policy.</summary>
public sealed class EscalationPolicy : Entity, IStamped
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Policy applies by default to tasks with these criticalities (comma list); explicit task policy wins.</summary>
    public string AppliesTo { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<EscalationStep> Steps { get; set; } = [];
}

public sealed class EscalationStep : Entity
{
    public Guid PolicyId { get; set; }
    public int Order { get; set; }
    public EscalationTrigger Trigger { get; set; }
    public double Threshold { get; set; }
    public ThresholdUnit Unit { get; set; } = ThresholdUnit.Days;
    public EscalationTarget Targets { get; set; }
    public NotifyChannels Channels { get; set; } = NotifyChannels.Email | NotifyChannels.Teams;
    /// <summary>Escalation level reached when this step fires (1 = assignee … 5 = CEO).</summary>
    public int Level { get; set; }
    public bool RaiseCeoAttention { get; set; }
}

/// <summary>
/// Persistent notification queue entry (spec §66). Every email / Teams message is queued here first,
/// so nothing is lost across restarts; the idempotency key makes enqueueing the same notice twice impossible.
/// </summary>
public sealed class Notification : Entity
{
    public string IdempotencyKey { get; set; } = "";
    public Guid? TaskId { get; set; }
    public Guid? RecipientEmployeeId { get; set; }
    public string? RecipientAddress { get; set; }
    public string? RecipientName { get; set; }
    public NotificationChannel Channel { get; set; }
    public NotificationCategory Category { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string? BodyHtml { get; set; }
    /// <summary>Target Teams chat / team+channel for group messages.</summary>
    public string? TargetConversation { get; set; }
    public int Priority { get; set; }
    public DateTime ScheduledAt { get; set; }
    /// <summary>Catch-up guard: a notification still unsent after this instant is expired, not sent (spec §62).</summary>
    public DateTime? ExpiresAt { get; set; }
    /// <summary>Notifications sharing a supersede key replace older pending ones (e.g. a newer reminder for the same task).</summary>
    public string? SupersedeKey { get; set; }
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? Error { get; set; }
    public DateTime? SentAt { get; set; }
    public string? ExternalMessageId { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Item in the CEO "Attention Required" queue (spec §39). One open item per dedupe key.</summary>
public sealed class AttentionItem : Entity
{
    public AttentionType Type { get; set; }
    public string DedupeKey { get; set; } = "";
    public Guid? TaskId { get; set; }
    public WorkTask? Task { get; set; }
    public Guid? CommitmentId { get; set; }
    public Guid? ExtensionRequestId { get; set; }
    public Guid? ApprovalId { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? SuggestionId { get; set; }
    /// <summary>Executive the item is for (null = all executives).</summary>
    public Guid? AudienceId { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public int Score { get; set; }
    public AttentionStatus Status { get; set; } = AttentionStatus.Open;
    public DateTime? SnoozedUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedById { get; set; }
    public string? Resolution { get; set; }
}
