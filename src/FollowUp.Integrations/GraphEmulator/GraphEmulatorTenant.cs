using System.Net;
using FollowUp.Core.Abstractions;
using FollowUp.Integrations.Graph;

namespace FollowUp.Integrations.GraphEmulator;

/// <summary>
/// <see cref="IDemoTenant"/> implemented directly against the emulator store (used by the demo seeder and the
/// simulator console). User ids are supplied by the caller; <see cref="GraphEmulatorIds.ForUser"/> gives the conventional
/// deterministic id for a mail address. Posts and mails accept explicit timestamps so history can be seeded.
/// </summary>
public sealed class GraphEmulatorTenant : IDemoTenant
{
    private readonly GraphEmulatorStore _store;

    internal GraphEmulatorTenant(GraphEmulatorStore store) => _store = store;

    public Task EnsureDirectoryAsync(IReadOnlyList<TenantUser> users, IReadOnlyList<TenantTeam> teams, IReadOnlyList<TenantChat> chats,
        CancellationToken ct) => _store.EnsureDirectoryAsync(users, teams, chats, ct);

    public async Task<string> SendMailAsync(TenantMail mail, CancellationToken ct)
    {
        if (mail.To.Count + mail.Cc.Count == 0) throw GraphEmulatorException.BadRequest("A mail needs at least one recipient.");
        var from = string.IsNullOrWhiteSpace(mail.FromAddress) ? mail.Mailbox : mail.FromAddress;
        if (string.IsNullOrWhiteSpace(from)) throw GraphEmulatorException.BadRequest("A mail needs a sender.");
        var sender = await _store.FindUserAsync(from, ct);
        var isHtml = LooksLikeHtml(mail.Body);
        var delivered = await _store.DeliverMailAsync(new MailDraft(
            SenderMailbox: string.IsNullOrWhiteSpace(mail.Mailbox) ? from : mail.Mailbox,
            FromAddress: from,
            FromName: sender?.DisplayName,
            To: await AddressesAsync(mail.To, ct),
            Cc: await AddressesAsync(mail.Cc, ct),
            Subject: mail.Subject,
            BodyContentType: isHtml ? "html" : "text",
            BodyContent: mail.Body,
            Importance: "normal",
            Headers: [],
            ExtendedProperties: [],
            SentAt: mail.SentAt == default ? _store.Now : mail.SentAt,
            ConversationId: mail.ConversationId,
            InReplyTo: mail.InReplyToInternetMessageId), ct);
        return delivered.InternetMessageId;
    }

    public async Task<string> PostChatMessageAsync(string chatId, TenantPost post, CancellationToken ct)
    {
        var message = await _store.PostChatMessageAsync(chatId, await DraftAsync(post, null, ct), ct);
        return message.MessageId;
    }

    public async Task<string> PostChannelMessageAsync(string teamId, string channelId, TenantPost post, CancellationToken ct)
    {
        var message = await _store.PostChannelMessageAsync(teamId, channelId, await DraftAsync(post, post.ReplyToId, ct), ct);
        return message.MessageId;
    }

    public async Task<string> EnsureOneOnOneChatAsync(string userId1, string userId2, CancellationToken ct)
    {
        var a = await _store.RequireUserAsync(userId1, ct);
        var b = await _store.RequireUserAsync(userId2, ct);
        var (chat, _) = await _store.EnsureOneOnOneChatAsync(a.Id, b.Id, ct);
        return chat.Id;
    }

    public async Task SetFaultsAsync(TenantFaults faults, CancellationToken ct) => await _store.SetFaultsAsync(faults, ct);

    public Task<TenantFaults> GetFaultsAsync(CancellationToken ct) => _store.GetFaultsAsync(ct);

    /// <summary>Plain text (or HTML) post with mentions resolved to directory display names.</summary>
    internal async Task<TeamsPostDraft> DraftAsync(TenantPost post, string? replyToId, CancellationToken ct, string importance = "normal",
        string? subject = null)
    {
        var sender = await _store.RequireUserAsync(post.FromUserId, ct);
        var mentioned = new List<(string UserId, string DisplayName)>();
        foreach (var id in (post.MentionUserIds ?? []).Distinct())
        {
            var user = await _store.RequireUserAsync(id, ct);
            mentioned.Add((user.Id, user.DisplayName));
        }
        var html = LooksLikeHtml(post.Text) ? post.Text : TeamsContent.TextToHtml(post.Text);
        var (content, mentions) = TeamsContent.WithMentions(html, mentioned);
        return new TeamsPostDraft(sender.Id, content, subject, importance, mentions, post.CreatedAt == default ? _store.Now : post.CreatedAt, replyToId);
    }

    private async Task<List<MailAddress>> AddressesAsync(IReadOnlyList<string> addresses, CancellationToken ct)
    {
        var result = new List<MailAddress>();
        foreach (var address in addresses.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            var user = await _store.FindUserAsync(address, ct);
            result.Add(new MailAddress(user?.Mail ?? address.Trim(), user?.DisplayName));
        }
        return result;
    }

    internal static bool LooksLikeHtml(string text) =>
        text.Contains('<') && text.Contains('>') && System.Text.RegularExpressions.Regex.IsMatch(text, @"<(p|div|br|html|body|span|b|i|a|ul|ol|li|table)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Plain text to a simple Outlook-style HTML body.</summary>
    internal static string TextToMailHtml(string text) =>
        "<html><body>" + string.Join("", text.Replace("\r\n", "\n").Split('\n').Select(l => $"<div>{(l.Length == 0 ? "<br>" : WebUtility.HtmlEncode(l))}</div>")) + "</body></html>";
}
