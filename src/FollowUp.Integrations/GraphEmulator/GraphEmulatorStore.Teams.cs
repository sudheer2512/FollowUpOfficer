using System.Globalization;
using FollowUp.Integrations.Graph;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Integrations.GraphEmulator;

internal sealed record StoredMention(int Id, string MentionText, string UserId, string DisplayName);

internal sealed record StoredTeamsAttachment(string Id, string? ContentType, string? ContentUrl, string? Name);

/// <summary>A Teams post to store. <see cref="ReplyToId"/> makes a channel reply.</summary>
internal sealed record TeamsPostDraft(
    string FromUserId,
    string BodyHtml,
    string? Subject,
    string Importance,
    IReadOnlyList<TeamsMentionTag> Mentions,
    DateTime CreatedAt,
    string? ReplyToId = null);

/// <summary>Keyset page request over Teams messages (newest first).</summary>
internal sealed record TeamsMessageQuery(int Top, bool ByLastModified, DateTime? ModifiedAfter, DateTime? ModifiedBefore, string? SkipToken);

internal sealed partial class GraphEmulatorStore
{
    public static string ChannelContainer(string teamId, string channelId) => $"{teamId}|{channelId}";

    // ── Chats ──

    public async Task<EmulatorChat> RequireChatAsync(string chatId, CancellationToken ct) =>
        await _db.Chats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chatId, ct) ?? throw GraphEmulatorException.NotFound(chatId);

    public Task<List<EmulatorChatMember>> ChatMembersAsync(IReadOnlyCollection<string> chatIds, CancellationToken ct) =>
        _db.ChatMembers.AsNoTracking().Where(m => chatIds.Contains(m.ChatId)).ToListAsync(ct);

    public Task<bool> IsChatMemberAsync(string chatId, string userId, CancellationToken ct) =>
        _db.ChatMembers.AsNoTracking().AnyAsync(m => m.ChatId == chatId && m.UserId == userId, ct);

    /// <summary>Chats, optionally only those of one member, most recently updated first.</summary>
    public Task<List<EmulatorChat>> ListChatsAsync(string? memberUserId, CancellationToken ct)
    {
        var query = _db.Chats.AsNoTracking();
        if (memberUserId is not null)
            query = query.Where(c => _db.ChatMembers.Any(m => m.ChatId == c.Id && m.UserId == memberUserId));
        return query.OrderByDescending(c => c.LastUpdatedAt).ThenBy(c => c.Id).ToListAsync(ct);
    }

    /// <summary>Returns the 1:1 chat of two users, creating it when needed (Created tells which).</summary>
    public Task<(EmulatorChat Chat, bool Created)> EnsureOneOnOneChatAsync(string userId1, string userId2, CancellationToken ct) =>
        WriteAsync(async () =>
        {
            if (userId1 == userId2) throw GraphEmulatorException.BadRequest("A 1:1 chat needs two different users.");
            foreach (var id in new[] { userId1, userId2 })
                if (!await _db.Users.AnyAsync(u => u.Id == id, ct)) throw GraphEmulatorException.NotFound(id);
            var key = GraphEmulatorIds.OneOnOneKey(userId1, userId2);
            var existing = await _db.Chats.AsNoTracking().FirstOrDefaultAsync(c => c.OneOnOneKey == key, ct);
            if (existing is not null) return (existing, false);
            var now = Now;
            var chat = new EmulatorChat
            {
                Id = GraphEmulatorIds.OneOnOneChat(userId1, userId2), ChatType = "oneOnOne", OneOnOneKey = key, CreatedAt = now, LastUpdatedAt = now,
            };
            _db.Chats.Add(chat);
            _db.ChatMembers.Add(new EmulatorChatMember { ChatId = chat.Id, UserId = userId1, JoinedAt = now });
            _db.ChatMembers.Add(new EmulatorChatMember { ChatId = chat.Id, UserId = userId2, JoinedAt = now });
            await _db.SaveChangesAsync(ct);
            return (chat, true);
        }, ct);

    /// <summary>Creates a group chat.</summary>
    public Task<EmulatorChat> CreateGroupChatAsync(string? topic, IReadOnlyList<string> memberIds, CancellationToken ct) => WriteAsync(async () =>
    {
        var members = memberIds.Distinct().ToList();
        if (members.Count < 2) throw GraphEmulatorException.BadRequest("A group chat needs at least two members.");
        foreach (var id in members)
            if (!await _db.Users.AnyAsync(u => u.Id == id, ct)) throw GraphEmulatorException.NotFound(id);
        var now = Now;
        var chat = new EmulatorChat { Id = GraphEmulatorIds.NewGroupChat(), ChatType = "group", Topic = topic, CreatedAt = now, LastUpdatedAt = now };
        _db.Chats.Add(chat);
        foreach (var id in members) _db.ChatMembers.Add(new EmulatorChatMember { ChatId = chat.Id, UserId = id, JoinedAt = now });
        await _db.SaveChangesAsync(ct);
        return chat;
    }, ct);

    /// <summary>Posts a chat message as a member of the chat.</summary>
    public Task<EmulatorTeamsMessage> PostChatMessageAsync(string chatId, TeamsPostDraft draft, CancellationToken ct) => WriteAsync(async () =>
    {
        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct) ?? throw GraphEmulatorException.NotFound(chatId);
        var sender = await FindUserAsync(draft.FromUserId, ct) ?? throw GraphEmulatorException.NotFound(draft.FromUserId);
        if (!await _db.ChatMembers.AnyAsync(m => m.ChatId == chatId && m.UserId == sender.Id, ct))
            throw GraphEmulatorException.Forbidden($"{sender.DisplayName} is not a member of chat {chatId}.");
        var message = await NewMessageAsync(chatId, draft, sender, ct);
        message.ChatId = chatId;
        _db.TeamsMessages.Add(message);
        chat.LastUpdatedAt = message.CreatedDateTime > chat.LastUpdatedAt ? message.CreatedDateTime : chat.LastUpdatedAt;
        await _db.SaveChangesAsync(ct);
        return message;
    }, ct);

    // ── Channels ──

    /// <summary>Posts a channel root message or a reply (a reply re-sequences its root so channel delta reports the thread).</summary>
    public Task<EmulatorTeamsMessage> PostChannelMessageAsync(string teamId, string channelId, TeamsPostDraft draft, CancellationToken ct) =>
        WriteAsync(async () =>
        {
            if (!await _db.Channels.AnyAsync(c => c.TeamId == teamId && c.Id == channelId, ct))
                throw GraphEmulatorException.NotFound($"{teamId}/channels/{channelId}");
            var sender = await FindUserAsync(draft.FromUserId, ct) ?? throw GraphEmulatorException.NotFound(draft.FromUserId);
            var container = ChannelContainer(teamId, channelId);
            EmulatorTeamsMessage? root = null;
            if (!string.IsNullOrWhiteSpace(draft.ReplyToId))
            {
                root = await _db.TeamsMessages.FirstOrDefaultAsync(m => m.ContainerId == container && m.MessageId == draft.ReplyToId, ct)
                       ?? throw GraphEmulatorException.NotFound($"messages/{draft.ReplyToId}");
                if (root.ReplyToId is not null)
                    root = await _db.TeamsMessages.FirstAsync(m => m.ContainerId == container && m.MessageId == root.ReplyToId, ct);
            }

            var message = await NewMessageAsync(container, draft with { ReplyToId = root?.MessageId }, sender, ct);
            message.TeamId = teamId;
            message.ChannelId = channelId;
            if (root is not null) root.ChangeSeq = await NextSequenceAsync(ct);
            _db.TeamsMessages.Add(message);
            await _db.SaveChangesAsync(ct);
            return message;
        }, ct);

    /// <summary>Channel delta over root messages (re-sequenced on replies).</summary>
    public async Task<DeltaResult<EmulatorTeamsMessage>> ChannelDeltaAsync(string teamId, string channelId, DeltaRequest request, CancellationToken ct)
    {
        var container = ChannelContainer(teamId, channelId);
        var resource = $"channel:{container}";
        var window = await ResolveDeltaWindowAsync(resource, request, ct);
        var query = _db.TeamsMessages.AsNoTracking()
            .Where(m => m.ContainerId == container && m.ReplyToId == null && m.ChangeSeq > window.Lower && m.ChangeSeq <= window.UpTo);
        if (window.Initial) query = query.Where(m => m.DeletedDateTime == null);
        if (window.Since is { } since)
            query = query.Where(m => m.LastModifiedDateTime > since
                                     || _db.TeamsMessages.Any(r => r.ContainerId == container && r.ReplyToId == m.MessageId && r.LastModifiedDateTime > since));
        var fetched = await query.OrderBy(m => m.ChangeSeq).Take(request.PageSize + 1).ToListAsync(ct);
        return BuildDeltaResult(resource, window, fetched, m => m.ChangeSeq, request.PageSize);
    }

    public async Task<EmulatorTeamsMessage> RequireTeamsMessageAsync(string container, string messageId, CancellationToken ct) =>
        await _db.TeamsMessages.AsNoTracking().FirstOrDefaultAsync(m => m.ContainerId == container && m.MessageId == messageId, ct)
        ?? throw GraphEmulatorException.NotFound($"messages/{messageId}");

    /// <summary>
    /// Keyset page of messages newest first: chat messages (rootsOnly false, replyToId null), channel roots
    /// (rootsOnly true) or the replies of one channel root (replyToId set).
    /// </summary>
    public async Task<(List<EmulatorTeamsMessage> Items, string? NextSkipToken)> ListTeamsMessagesAsync(string container, bool rootsOnly,
        string? replyToId, TeamsMessageQuery q, CancellationToken ct)
    {
        var query = _db.TeamsMessages.AsNoTracking().Where(m => m.ContainerId == container);
        if (replyToId is not null) query = query.Where(m => m.ReplyToId == replyToId);
        else if (rootsOnly) query = query.Where(m => m.ReplyToId == null);
        if (q.ModifiedAfter is { } after) query = query.Where(m => m.LastModifiedDateTime > after);
        if (q.ModifiedBefore is { } before) query = query.Where(m => m.LastModifiedDateTime < before);
        if (ParseKeyset(q.SkipToken) is { } key)
        {
            var (time, pk) = key;
            query = q.ByLastModified
                ? query.Where(m => m.LastModifiedDateTime < time || (m.LastModifiedDateTime == time && m.Pk < pk))
                : query.Where(m => m.CreatedDateTime < time || (m.CreatedDateTime == time && m.Pk < pk));
        }
        query = q.ByLastModified
            ? query.OrderByDescending(m => m.LastModifiedDateTime).ThenByDescending(m => m.Pk)
            : query.OrderByDescending(m => m.CreatedDateTime).ThenByDescending(m => m.Pk);
        var items = await query.Take(q.Top + 1).ToListAsync(ct);
        if (items.Count <= q.Top) return (items, null);
        items.RemoveAt(items.Count - 1);
        var last = items[^1];
        var lastTime = q.ByLastModified ? last.LastModifiedDateTime : last.CreatedDateTime;
        return (items, $"{lastTime.Ticks.ToString(CultureInfo.InvariantCulture)}.{last.Pk.ToString(CultureInfo.InvariantCulture)}");
    }

    public Task<List<EmulatorTeamsMessage>> RepliesAsync(string container, IReadOnlyCollection<string> rootIds, CancellationToken ct) =>
        _db.TeamsMessages.AsNoTracking()
            .Where(m => m.ContainerId == container && m.ReplyToId != null && rootIds.Contains(m.ReplyToId))
            .OrderBy(m => m.CreatedDateTime).ThenBy(m => m.Pk)
            .ToListAsync(ct);

    private static (DateTime Time, long Pk)? ParseKeyset(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                              && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pk))
            return (new DateTime(ticks, DateTimeKind.Utc), pk);
        throw GraphEmulatorException.BadRequest("Invalid $skiptoken.");
    }

    /// <summary>Builds a message with a container-unique, epoch-milliseconds id.</summary>
    private async Task<EmulatorTeamsMessage> NewMessageAsync(string container, TeamsPostDraft draft, EmulatorUser sender, CancellationToken ct)
    {
        var created = draft.CreatedAt == default ? Now : GraphJson.AsUtc(draft.CreatedAt);
        var number = long.Parse(GraphEmulatorIds.TeamsMessageId(created), CultureInfo.InvariantCulture);
        var id = number.ToString(CultureInfo.InvariantCulture);
        while (_db.TeamsMessages.Local.Any(m => m.ContainerId == container && m.MessageId == id)
               || await _db.TeamsMessages.AnyAsync(m => m.ContainerId == container && m.MessageId == id, ct))
            id = (++number).ToString(CultureInfo.InvariantCulture);
        return new EmulatorTeamsMessage
        {
            ContainerId = container,
            MessageId = id,
            ReplyToId = draft.ReplyToId,
            FromUserId = sender.Id,
            FromDisplayName = sender.DisplayName,
            Subject = string.IsNullOrWhiteSpace(draft.Subject) ? null : draft.Subject,
            BodyContentType = "html",
            BodyContent = draft.BodyHtml,
            Importance = NormalizeTeamsImportance(draft.Importance),
            MentionsJson = ToJson(draft.Mentions.Select(m => new StoredMention(m.Id, m.DisplayName, m.UserId, m.DisplayName)).ToList()),
            AttachmentsJson = "[]",
            CreatedDateTime = created,
            LastModifiedDateTime = created,
            ChangeSeq = await NextSequenceAsync(ct),
        };
    }

    internal static string NormalizeTeamsImportance(string? importance) => importance?.Trim().ToLowerInvariant() switch
    {
        "urgent" => "urgent",
        "high" => "high",
        _ => "normal",
    };

    // ── Activity feed ──

    public Task<EmulatorActivityNotification> AddActivityAsync(EmulatorActivityNotification notification, CancellationToken ct) =>
        WriteAsync(async () =>
        {
            notification.CreatedDateTime = Now;
            _db.ActivityNotifications.Add(notification);
            await _db.SaveChangesAsync(ct);
            return notification;
        }, ct);

    public Task<List<EmulatorActivityNotification>> ListActivityAsync(string? userId, int max, CancellationToken ct) =>
        _db.ActivityNotifications.AsNoTracking()
            .Where(a => userId == null || a.UserId == userId)
            .OrderByDescending(a => a.Id)
            .Take(max)
            .ToListAsync(ct);
}
