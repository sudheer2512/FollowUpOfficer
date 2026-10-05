namespace FollowUp.Integrations.GraphEmulator;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// Graph emulator store (schema "m365emu"). A small Microsoft 365 tenant: directory, Teams, chats, mailboxes,
// calendars, activity feed, issued tokens and fault settings. Mail and channel messages carry a tenant-wide,
// monotonically increasing change sequence that the delta endpoints page over.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Directory user (also the owner of a mailbox and calendar). Mail and UPN are stored lower-case.</summary>
public sealed class EmulatorUser
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Mail { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? ManagerId { get; set; }
    public bool AccountEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

/// <summary>A team (a Microsoft 365 group provisioned for Teams).</summary>
public sealed class EmulatorTeam
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A channel of a team.</summary>
public sealed class EmulatorChannel
{
    public string TeamId { get; set; } = "";
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Team membership.</summary>
public sealed class EmulatorTeamMember
{
    public string TeamId { get; set; } = "";
    public string UserId { get; set; } = "";
}

/// <summary>Chat: "oneOnOne" (unique per user pair through <see cref="OneOnOneKey"/>) or "group".</summary>
public sealed class EmulatorChat
{
    public string Id { get; set; } = "";
    public string ChatType { get; set; } = "group";
    public string? Topic { get; set; }
    /// <summary>"{userIdA}|{userIdB}" (ordinal order) for oneOnOne chats.</summary>
    public string? OneOnOneKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}

/// <summary>Chat membership.</summary>
public sealed class EmulatorChatMember
{
    public string ChatId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Roles { get; set; } = "owner";
    public DateTime JoinedAt { get; set; }
}

/// <summary>A copy of a message in one mailbox folder (inbox or sentitems).</summary>
public sealed class EmulatorMailItem
{
    public long Pk { get; set; }
    public string Id { get; set; } = "";
    public string Mailbox { get; set; } = "";
    public string Folder { get; set; } = "inbox";
    public string InternetMessageId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string BodyContentType { get; set; } = "html";
    public string BodyContent { get; set; } = "";
    public string BodyPreview { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string? FromName { get; set; }
    /// <summary>JSON array of {address,name}.</summary>
    public string ToJson { get; set; } = "[]";
    public string CcJson { get; set; } = "[]";
    /// <summary>JSON array of {name,value} internet message headers.</summary>
    public string HeadersJson { get; set; } = "[]";
    /// <summary>JSON array of {id,value} single-value extended properties.</summary>
    public string ExtendedPropertiesJson { get; set; } = "[]";
    public string? InReplyTo { get; set; }
    public string? References { get; set; }
    public string Importance { get; set; } = "normal";
    public bool HasAttachments { get; set; }
    public bool IsRead { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime SentDateTime { get; set; }
    public DateTime ReceivedDateTime { get; set; }
    public DateTime CreatedDateTime { get; set; }
    public DateTime LastModifiedDateTime { get; set; }
    public long ChangeSeq { get; set; }
}

/// <summary>Chat message, channel root post or channel reply.</summary>
public sealed class EmulatorTeamsMessage
{
    public long Pk { get; set; }
    /// <summary>Chat id, or "{teamId}|{channelId}" for channel messages.</summary>
    public string ContainerId { get; set; } = "";
    public string? ChatId { get; set; }
    public string? TeamId { get; set; }
    public string? ChannelId { get; set; }
    public string MessageId { get; set; } = "";
    public string? ReplyToId { get; set; }
    public string MessageType { get; set; } = "message";
    public string? FromUserId { get; set; }
    public string? FromDisplayName { get; set; }
    public string? Subject { get; set; }
    public string BodyContentType { get; set; } = "html";
    public string BodyContent { get; set; } = "";
    public string Importance { get; set; } = "normal";
    /// <summary>JSON array of {id,mentionText,userId,displayName}.</summary>
    public string MentionsJson { get; set; } = "[]";
    /// <summary>JSON array of {id,contentType,contentUrl,name}.</summary>
    public string AttachmentsJson { get; set; } = "[]";
    public DateTime CreatedDateTime { get; set; }
    public DateTime LastModifiedDateTime { get; set; }
    public DateTime? DeletedDateTime { get; set; }
    /// <summary>Change sequence (channel delta). A channel root is re-sequenced when a reply is posted.</summary>
    public long ChangeSeq { get; set; }
}

/// <summary>Calendar event of a mailbox (start/end kept as Graph dateTime + timeZone strings).</summary>
public sealed class EmulatorEvent
{
    public string Id { get; set; } = "";
    public string Mailbox { get; set; } = "";
    public string? TransactionId { get; set; }
    public string Subject { get; set; } = "";
    public string BodyContentType { get; set; } = "text";
    public string BodyContent { get; set; } = "";
    public string StartDateTime { get; set; } = "";
    public string StartTimeZone { get; set; } = "UTC";
    public string EndDateTime { get; set; } = "";
    public string EndTimeZone { get; set; } = "UTC";
    public bool IsAllDay { get; set; }
    public string Importance { get; set; } = "normal";
    public bool IsReminderOn { get; set; }
    public int ReminderMinutesBeforeStart { get; set; }
    public string ShowAs { get; set; } = "busy";
    public DateTime CreatedDateTime { get; set; }
    public DateTime LastModifiedDateTime { get; set; }
}

/// <summary>Teams activity-feed notification delivered to a user.</summary>
public sealed class EmulatorActivityNotification
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string ActivityType { get; set; } = "";
    public string? TopicValue { get; set; }
    public string? TopicWebUrl { get; set; }
    public string? PreviewText { get; set; }
    /// <summary>JSON array of {name,value}.</summary>
    public string TemplateParametersJson { get; set; } = "[]";
    public string? SenderAppId { get; set; }
    public DateTime CreatedDateTime { get; set; }
}

/// <summary>Access / refresh token issued by the emulator's token endpoint (SHA-256 hash only).</summary>
public sealed class EmulatorIssuedToken
{
    public string TokenHash { get; set; } = "";
    /// <summary>"app", "delegated" or "refresh".</summary>
    public string Kind { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? UserId { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Singleton row: fault injection, delta generation and the change-sequence counter.</summary>
public sealed class EmulatorSettings
{
    public int Id { get; set; } = 1;
    public bool Outage { get; set; }
    public int ThrottlePercent { get; set; }
    public bool AuthFailure { get; set; }
    public int LatencyMs { get; set; }
    /// <summary>Incremented to invalidate every outstanding delta token.</summary>
    public int DeltaEpoch { get; set; } = 1;
    public long ChangeSequence { get; set; }
    public DateTime UpdatedAt { get; set; }
}
