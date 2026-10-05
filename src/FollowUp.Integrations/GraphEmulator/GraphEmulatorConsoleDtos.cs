namespace FollowUp.Integrations.GraphEmulator;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// DTOs of the "Microsoft 365 Simulator" console API (camelCase JSON). Times are UTC.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>A tenant user.</summary>
public sealed record SimUserDto(string Id, string DisplayName, string Mail, string? JobTitle, string? Department, string? ManagerId);

/// <summary>A mail recipient.</summary>
public sealed record SimRecipientDto(string Address, string? Name);

/// <summary>A mailbox item. FollowUpKey is the X-FollowUp-Key of reminders sent by the application (null for people's mail).</summary>
public sealed record SimMailDto(
    string Id,
    string InternetMessageId,
    string ConversationId,
    string Mailbox,
    string Folder,
    string Subject,
    string BodyText,
    string BodyPreview,
    string FromAddress,
    string? FromName,
    IReadOnlyList<SimRecipientDto> To,
    IReadOnlyList<SimRecipientDto> Cc,
    DateTime SentAt,
    DateTime ReceivedAt,
    string Importance,
    string? InReplyTo,
    string? FollowUpKey,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>A user @mentioned in a Teams message.</summary>
public sealed record SimMentionDto(string UserId, string DisplayName);

/// <summary>A chat message or channel post; channel roots carry their replies.</summary>
public sealed record SimTeamsMessageDto(
    string Id,
    string? ReplyToId,
    string? FromUserId,
    string? FromName,
    string? Subject,
    string BodyHtml,
    string BodyText,
    string Importance,
    DateTime CreatedAt,
    DateTime LastModifiedAt,
    bool IsDeleted,
    IReadOnlyList<SimMentionDto> Mentions,
    IReadOnlyList<SimTeamsMessageDto> Replies);

/// <summary>A chat member.</summary>
public sealed record SimChatMemberDto(string UserId, string DisplayName, string? Mail);

/// <summary>A chat with its members and latest message.</summary>
public sealed record SimChatDto(
    string Id,
    string ChatType,
    string? Topic,
    string DisplayName,
    IReadOnlyList<SimChatMemberDto> Members,
    SimTeamsMessageDto? LastMessage,
    DateTime LastUpdatedAt);

/// <summary>A team channel with its message count (roots and replies) and last activity.</summary>
public sealed record SimChannelDto(string TeamId, string TeamName, string ChannelId, string ChannelName, int MessageCount, DateTime? LastActivityAt);

/// <summary>A Teams activity-feed notification; Text is the systemDefaultText template parameter.</summary>
public sealed record SimActivityDto(long Id, string UserId, string? UserName, string ActivityType, string? Topic, string? PreviewText,
    string? Text, DateTime CreatedAt);

/// <summary>Current fault injection settings and the delta-token generation.</summary>
public sealed record SimFaultsDto(bool Outage, int ThrottlePercent, bool AuthFailure, int LatencyMs, int DeltaEpoch);

/// <summary>Fault injection settings to apply (ThrottlePercent 0–100, LatencyMs 0–60000).</summary>
public sealed record SimFaultsRequest(bool Outage, int ThrottlePercent, bool AuthFailure, int LatencyMs);

/// <summary>
/// Compose a mail as a tenant user. Recipients are user ids or addresses (external addresses are accepted and not delivered).
/// InReplyToMessageId accepts an Internet-Message-Id or a Graph item id; the reply joins the conversation, defaults the
/// subject to "RE: …" and (QuoteOriginal) appends the original Outlook-style.
/// </summary>
public sealed record SimComposeMailRequest(
    string From,
    IReadOnlyList<string> To,
    IReadOnlyList<string>? Cc,
    string? Subject,
    string Body,
    string? InReplyToMessageId = null,
    bool HighImportance = false,
    bool QuoteOriginal = true);

/// <summary>Result of a composed mail: its ids and the tenant mailboxes it was delivered to.</summary>
public sealed record SimMailSentDto(string InternetMessageId, string ConversationId, IReadOnlyList<string> DeliveredTo);

/// <summary>Post a chat message as a member (user id or mail).</summary>
public sealed record SimChatPostRequest(string FromUserId, string Text, IReadOnlyList<string>? MentionUserIds = null, bool Urgent = false);

/// <summary>Post a channel message as a user; ReplyToId makes it a reply to that thread.</summary>
public sealed record SimChannelPostRequest(string FromUserId, string Text, string? ReplyToId = null, string? Subject = null,
    IReadOnlyList<string>? MentionUserIds = null, bool Urgent = false);

/// <summary>Find or create the 1:1 chat of two users (ids or mails).</summary>
public sealed record SimOneOnOneRequest(string UserA, string UserB);

/// <summary>Id of a posted Teams message.</summary>
public sealed record SimPostResultDto(string MessageId);

/// <summary>New delta-token generation after invalidation.</summary>
public sealed record SimDeltaInvalidatedDto(int DeltaEpoch);
