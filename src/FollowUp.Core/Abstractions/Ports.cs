using FollowUp.Domain;

namespace FollowUp.Core.Abstractions;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// Integration ports (spec §77). Core task logic depends only on these interfaces; Microsoft Graph code
// lives in FollowUp.Integrations. Implementations throw IntegrationException with a classified kind so
// Core can record integration errors, back off on throttling and recover from expired delta tokens.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Classified failure from an external system (Graph, AI provider…).</summary>
public sealed class IntegrationException(IntegrationErrorKind kind, string message, int? statusCode = null,
    TimeSpan? retryAfter = null, Exception? inner = null) : Exception(message, inner)
{
    public IntegrationErrorKind Kind { get; } = kind;
    public int? StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool IsTransient => Kind is IntegrationErrorKind.Throttled or IntegrationErrorKind.Transient;
}

public sealed record EmailAddress(string Address, string? Name = null);

/// <summary>Outbound email. The idempotency key is stamped on the message so a crash between send and commit can be reconciled.</summary>
public sealed record OutboundEmail(
    string IdempotencyKey,
    IReadOnlyList<EmailAddress> To,
    IReadOnlyList<EmailAddress> Cc,
    string Subject,
    string BodyText,
    string? BodyHtml = null,
    bool HighImportance = false,
    string? FromMailbox = null);

public enum TeamsTargetKind { User, GroupChat, Channel, ActivityFeed }

public sealed record TeamsMention(string UserId, string DisplayName);

/// <summary>Outbound Teams message to a person (1:1 chat), a group chat, a channel or the activity feed.</summary>
public sealed record OutboundTeamsMessage(
    string IdempotencyKey,
    TeamsTargetKind Target,
    string Text,
    string? Html = null,
    string? UserId = null,
    string? UserAddress = null,
    string? ChatId = null,
    string? TeamId = null,
    string? ChannelId = null,
    bool Urgent = false,
    IReadOnlyList<TeamsMention>? Mentions = null);

public sealed record DeliveryResult(bool Success, string? ExternalMessageId, string? Error = null);

/// <summary>Sends email (spec §19).</summary>
public interface IEmailService
{
    Task<DeliveryResult> SendAsync(OutboundEmail email, CancellationToken ct);

    /// <summary>
    /// Reconciliation after a crash between "send" and "mark sent": was a message with this idempotency key
    /// already sent since <paramref name="sinceUtc"/>? Null when the provider cannot tell.
    /// </summary>
    Task<bool?> WasSentAsync(string idempotencyKey, DateTime sinceUtc, CancellationToken ct);
}

/// <summary>Sends Microsoft Teams messages (spec §20).</summary>
public interface ITeamsService
{
    Task<DeliveryResult> SendAsync(OutboundTeamsMessage message, CancellationToken ct);
}

// ── Inbound synchronisation (spec §21, §25, §60–§64) ──

/// <summary>
/// Durable position in a source. Mail and channel messages use Graph delta (DeltaToken = deltaLink,
/// ContinuationToken = nextLink); chats use a timestamp watermark with paging.
/// </summary>
public sealed record SyncCursor(
    IntegrationType Type,
    string SourceKey,
    string? DeltaToken,
    string? ContinuationToken,
    DateTime? Watermark,
    string? LastExternalId);

/// <summary>
/// One page of changes. Core imports the messages and persists the returned cursor values in the same
/// transaction, then asks for the next page until <see cref="IsFinal"/>.
/// </summary>
public sealed record SyncPage(
    IReadOnlyList<InboundMessage> Messages,
    string? ContinuationToken,
    string? DeltaToken,
    DateTime? Watermark,
    bool IsFinal);

public sealed record InboundMessage
{
    public CommunicationChannel Channel { get; init; }
    public string SourceKey { get; init; } = "";
    /// <summary>Unique id: Internet-Message-Id for email; Graph message id for Teams.</summary>
    public string ExternalId { get; init; } = "";
    public string? GraphItemId { get; init; }
    /// <summary>Email: conversationId. Chat: chat id. Channel: team|channel|threadRootId.</summary>
    public string? ConversationId { get; init; }
    /// <summary>Partition for uniqueness: "mail", chat id or "teamId|channelId".</summary>
    public string ConversationKey { get; init; } = "";
    public string? ThreadRootId { get; init; }
    public string? ReplyToId { get; init; }
    public string? Subject { get; init; }
    public string BodyText { get; init; } = "";
    public string? FromAddress { get; init; }
    public string? FromName { get; init; }
    public string? FromUserId { get; init; }
    public IReadOnlyList<MessageParty> To { get; init; } = [];
    public IReadOnlyList<MessageParty> Cc { get; init; } = [];
    public IReadOnlyList<MessageParty> Mentions { get; init; } = [];
    public IReadOnlyList<MessageAttachmentRef> Attachments { get; init; } = [];
    public DateTime SentAt { get; init; }
    public DateTime LastModifiedAt { get; init; }
    public string? WebUrl { get; init; }
    public string? Importance { get; init; }
    public bool IsDeleted { get; init; }
    /// <summary>Selected headers (e.g. X-FollowUp-Key on our own reminders, In-Reply-To).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>A source the connected account is authorised to monitor.</summary>
public sealed record SyncSourceInfo(IntegrationType Type, string SourceKey, string DisplayName, string? OwnerAddress);

/// <summary>Outlook mailbox synchronisation. Source key: "{mailbox}|{folder}" with folder inbox or sentitems.</summary>
public interface IMailSynchronizationService
{
    Task<IReadOnlyList<SyncSourceInfo>> DiscoverSourcesAsync(CancellationToken ct);

    /// <summary>Next page after the cursor. Throws IntegrationException(Gone) when the delta token expired (Core then resyncs from the watermark).</summary>
    Task<SyncPage> FetchAsync(SyncCursor cursor, CancellationToken ct);
}

/// <summary>Teams chats and channel synchronisation. Source keys: chat id, or "{teamId}|{channelId}".</summary>
public interface ITeamsSynchronizationService
{
    Task<IReadOnlyList<SyncSourceInfo>> DiscoverSourcesAsync(CancellationToken ct);
    Task<SyncPage> FetchAsync(SyncCursor cursor, CancellationToken ct);
}

// ── Calendar (spec §72) ──

public sealed record CalendarEventRequest(
    string Key,
    string MailboxAddress,
    string? ExistingEventId,
    string Subject,
    string BodyText,
    DateOnly Date,
    TimeOnly? Start,
    TimeOnly? End,
    string TimeZoneId,
    bool HighImportance,
    int? ReminderMinutesBefore);

public interface ICalendarService
{
    /// <summary>Creates or updates the event; returns the Graph event id.</summary>
    Task<string> UpsertEventAsync(CalendarEventRequest request, CancellationToken ct);
    Task DeleteEventAsync(string mailboxAddress, string eventId, CancellationToken ct);
}

// ── Directory (spec §71) ──

public sealed record DirectoryUser(string ExternalId, string DisplayName, string? Mail, string? UserPrincipalName,
    string? JobTitle, string? Department, bool AccountEnabled, string? ManagerExternalId);

public sealed record DirectoryChannel(string ChannelId, string DisplayName);

public sealed record DirectoryTeam(string TeamId, string DisplayName, IReadOnlyList<DirectoryChannel> Channels);

public interface IUserDirectoryService
{
    Task<IReadOnlyList<DirectoryUser>> ListUsersAsync(CancellationToken ct);
    Task<IReadOnlyList<DirectoryTeam>> ListTeamsAsync(CancellationToken ct);
}

/// <summary>Connection health of the Microsoft 365 integration for the admin monitor (spec §67).</summary>
public sealed record IntegrationProbe(string Mode, bool Authenticated, string? TenantId, string? Account,
    IReadOnlyList<string> GrantedPermissions, IReadOnlyList<string> MissingPermissions, string? Error, DateTime CheckedAt);

public interface IIntegrationHealth
{
    Task<IntegrationProbe> ProbeAsync(CancellationToken ct);
}

// ── Platform ──

/// <summary>Secure attachment storage (spec §69). Implementations encrypt at rest.</summary>
public interface IFileStorage
{
    Task<StoredFile> SaveAsync(Stream content, string fileName, CancellationToken ct);
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct);
    Task DeleteAsync(string storageKey, CancellationToken ct);
}

public sealed record StoredFile(string StorageKey, long SizeBytes, string Sha256);

/// <summary>Protects secrets at rest (Graph client secret, delegated refresh token).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
