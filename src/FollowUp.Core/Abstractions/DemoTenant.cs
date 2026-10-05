namespace FollowUp.Core.Abstractions;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// Demo Microsoft 365 tenant port. Implemented by the Graph emulator in FollowUp.Integrations, which
// serves Graph-shaped REST endpoints so the real Graph client code path is exercised end to end.
// Only registered when Microsoft365:Mode = Emulator; the demo seeder and the simulator console use it.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

public sealed record TenantUser(string Id, string DisplayName, string Mail, string? JobTitle, string? Department, string? ManagerId = null);

public sealed record TenantChannel(string Id, string DisplayName);

public sealed record TenantTeam(string Id, string DisplayName, IReadOnlyList<TenantChannel> Channels, IReadOnlyList<string> MemberIds);

public sealed record TenantChat(string Id, string ChatType, string? Topic, IReadOnlyList<string> MemberIds);

public sealed record TenantMail(
    string Mailbox,
    string FromAddress,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    string Subject,
    string Body,
    DateTime SentAt,
    string? ConversationId = null,
    string? InReplyToInternetMessageId = null);

public sealed record TenantPost(string FromUserId, string Text, DateTime CreatedAt, string? ReplyToId = null, IReadOnlyList<string>? MentionUserIds = null);

public interface IDemoTenant
{
    /// <summary>Creates or updates directory users, teams, channels and chats (idempotent).</summary>
    Task EnsureDirectoryAsync(IReadOnlyList<TenantUser> users, IReadOnlyList<TenantTeam> teams, IReadOnlyList<TenantChat> chats, CancellationToken ct);

    /// <summary>Delivers a mail as if sent by FromAddress: lands in the sender's Sent Items and every recipient's Inbox. Returns the Internet-Message-Id.</summary>
    Task<string> SendMailAsync(TenantMail mail, CancellationToken ct);

    /// <summary>Posts a chat message as a user. Returns the message id.</summary>
    Task<string> PostChatMessageAsync(string chatId, TenantPost post, CancellationToken ct);

    /// <summary>Posts a channel message or reply as a user. Returns the message id.</summary>
    Task<string> PostChannelMessageAsync(string teamId, string channelId, TenantPost post, CancellationToken ct);

    /// <summary>Returns the 1:1 chat between two users, creating it if needed.</summary>
    Task<string> EnsureOneOnOneChatAsync(string userId1, string userId2, CancellationToken ct);

    /// <summary>Fault injection for resilience demos: outage, throttling percentage, token failures.</summary>
    Task SetFaultsAsync(TenantFaults faults, CancellationToken ct);

    Task<TenantFaults> GetFaultsAsync(CancellationToken ct);
}

public sealed record TenantFaults(bool Outage, int ThrottlePercent, bool AuthFailure, int LatencyMs);
