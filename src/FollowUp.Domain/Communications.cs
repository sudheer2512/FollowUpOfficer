namespace FollowUp.Domain;

/// <summary>
/// An email or Microsoft Teams message imported from Microsoft Graph (spec §21, §25, §26).
/// Emails and Teams messages share one table so correlation, classification, search and the task
/// timeline run one pipeline. Duplicate imports are impossible: (Channel, ConversationKey, ExternalId) is unique.
/// </summary>
public sealed class CommunicationMessage : Entity
{
    public CommunicationChannel Channel { get; set; }
    /// <summary>Email: "mail" (ExternalId = Internet-Message-Id, unique across mailboxes); chat: chat id; channel: teamId/channelId.</summary>
    public string ConversationKey { get; set; } = "";
    public string ExternalId { get; set; } = "";
    /// <summary>Graph item id in the mailbox / chat where it was first seen.</summary>
    public string? GraphItemId { get; set; }
    /// <summary>Email conversationId, Teams chat id or channel thread root id.</summary>
    public string? ConversationId { get; set; }
    public string? ThreadRootExternalId { get; set; }
    public string? ReplyToExternalId { get; set; }
    /// <summary>Monitored source the message was synchronised from (checkpoint source key).</summary>
    public string SourceKey { get; set; } = "";
    public string? Subject { get; set; }
    public string BodyText { get; set; } = "";
    public string? BodyPreview { get; set; }
    public Guid? SenderEmployeeId { get; set; }
    public Employee? SenderEmployee { get; set; }
    public string? SenderAddress { get; set; }
    public string? SenderName { get; set; }
    public List<MessageParty> Recipients { get; set; } = [];
    public List<MessageParty> Mentions { get; set; } = [];
    public List<MessageAttachmentRef> Attachments { get; set; } = [];
    public MessageDirection Direction { get; set; }
    public DateTime SentAt { get; set; }
    public DateTime ImportedAt { get; set; }
    public string? WebUrl { get; set; }
    public string? Importance { get; set; }
    /// <summary>True for messages this application sent itself (reminders) — never re-processed (loop guard).</summary>
    public bool IsSystemGenerated { get; set; }
    /// <summary>Notification idempotency key found in the message (reply to a reminder).</summary>
    public string? NotificationKey { get; set; }

    // Processing (AI classification / correlation pipeline)
    public MessageProcessingStatus ProcessingStatus { get; set; } = MessageProcessingStatus.Pending;
    public int ProcessingAttempts { get; set; }
    public DateTime? NextProcessingAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? ProcessingError { get; set; }
    public MessageCategory Category { get; set; } = MessageCategory.Unclassified;
    public double? CategoryConfidence { get; set; }
    public string? AnalysisSummary { get; set; }
    public MessageFacts? Facts { get; set; }
}

public sealed record MessageParty(string? Address, string? Name, string? ExternalUserId = null, Guid? EmployeeId = null, string? Kind = null);

public sealed record MessageAttachmentRef(string Name, string? ContentType, long Size, string? ExternalId);

/// <summary>Facts extracted from a message (spec §23): suggested status, blocker, commitment date…</summary>
public sealed record MessageFacts
{
    public WorkStatus? SuggestedStatus { get; init; }
    public string? Blocker { get; init; }
    public DateOnly? CommitmentDate { get; init; }
    public string? CommitmentText { get; init; }
    public DateOnly? ProposedDueDate { get; init; }
    public int? ProgressPercent { get; init; }
    public bool Urgent { get; init; }
    public List<string> Signals { get; init; } = [];
}

/// <summary>Association between a message and a task with confidence and matching signals (spec §27).</summary>
public sealed class TaskCommunicationLink : Entity
{
    public Guid TaskId { get; set; }
    public WorkTask? Task { get; set; }
    public Guid MessageId { get; set; }
    public CommunicationMessage? Message { get; set; }
    public MatchMethod Method { get; set; }
    public double Confidence { get; set; }
    public MessageCategory Category { get; set; }
    public string Explanation { get; set; } = "";
    public List<string> Signals { get; set; } = [];
    public LinkStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? DecidedById { get; set; }
    public DateTime? DecidedAt { get; set; }
}

/// <summary>
/// A conversation (email thread, Teams chat or channel thread) strongly associated with a task (spec §24).
/// Later messages in the same conversation are matched deterministically.
/// </summary>
public sealed class TaskConversation : Entity
{
    public Guid TaskId { get; set; }
    public CommunicationChannel Channel { get; set; }
    public string ConversationId { get; set; } = "";
    public string? Title { get; set; }
    public MatchMethod Method { get; set; }
    public double Strength { get; set; }
    public int MessageCount { get; set; }
    public DateTime LinkedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
}
