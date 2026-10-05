using FollowUp.Core.Abstractions;
using FollowUp.Domain;

namespace FollowUp.Integrations.Graph;

/// <summary>Maps Graph messages to <see cref="InboundMessage"/>.</summary>
internal static class GraphMessageMapper
{
    /// <summary>Headers copied to <see cref="InboundMessage.Headers"/> (our reminder key and threading headers).</summary>
    public static readonly string[] CapturedHeaders = [GraphEmailService.FollowUpKeyHeader, "In-Reply-To", "References"];

    public const string MailConversationKey = "mail";

    public static InboundMessage FromMail(GraphMessage m, string sourceKey, DateTime now, IReadOnlyList<MessageAttachmentRef>? attachments = null)
    {
        var graphId = m.Id ?? "";
        if (m.Removed is not null)
        {
            // Delta "@removed" entries carry only the Graph id.
            return new InboundMessage
            {
                Channel = CommunicationChannel.Email,
                SourceKey = sourceKey,
                ExternalId = graphId,
                GraphItemId = graphId,
                ConversationKey = MailConversationKey,
                IsDeleted = true,
                SentAt = now,
                LastModifiedAt = now,
            };
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in m.InternetMessageHeaders ?? [])
            if (CapturedHeaders.Contains(h.Name, StringComparer.OrdinalIgnoreCase) && !headers.ContainsKey(h.Name))
                headers[CapturedHeaders.First(c => c.Equals(h.Name, StringComparison.OrdinalIgnoreCase))] = h.Value;
        // Our reminders also carry the key as an extended property (present when the item was read with $expand).
        var keyProperty = m.SingleValueExtendedProperties?.FirstOrDefault(p =>
            p.Id.Equals(GraphEmailService.FollowUpKeyPropertyId, StringComparison.OrdinalIgnoreCase));
        if (keyProperty?.Value is { Length: > 0 } key && !headers.ContainsKey(GraphEmailService.FollowUpKeyHeader))
            headers[GraphEmailService.FollowUpKeyHeader] = key;

        var sent = (m.SentDateTime ?? m.ReceivedDateTime ?? m.CreatedDateTime)?.UtcDateTime ?? now;
        var body = HtmlText.MailBodyToText(m.Body?.Content, m.Body?.ContentType);
        if (body.Length == 0 && !string.IsNullOrWhiteSpace(m.BodyPreview)) body = m.BodyPreview.Trim();
        return new InboundMessage
        {
            Channel = CommunicationChannel.Email,
            SourceKey = sourceKey,
            ExternalId = string.IsNullOrWhiteSpace(m.InternetMessageId) ? graphId : m.InternetMessageId,
            GraphItemId = graphId,
            ConversationId = m.ConversationId,
            ConversationKey = MailConversationKey,
            Subject = m.Subject,
            BodyText = body,
            FromAddress = m.From?.EmailAddress?.Address ?? m.Sender?.EmailAddress?.Address,
            FromName = m.From?.EmailAddress?.Name ?? m.Sender?.EmailAddress?.Name,
            To = Parties(m.ToRecipients, "to"),
            Cc = Parties(m.CcRecipients, "cc"),
            Attachments = attachments ?? [],
            SentAt = sent,
            LastModifiedAt = m.LastModifiedDateTime?.UtcDateTime ?? sent,
            WebUrl = m.WebLink,
            Importance = m.Importance,
            Headers = headers,
        };
    }

    /// <summary>Maps a Teams chat / channel message; null for system and event messages.</summary>
    public static InboundMessage? FromTeams(GraphChatMessage m, string sourceKey, CommunicationChannel channel,
        string conversationKey, string? conversationId, string? threadRootId, DateTime now)
    {
        var id = m.Id ?? "";
        var deleted = m.Removed is not null || m.DeletedDateTime is not null;
        if (!deleted && !string.Equals(m.MessageType ?? "message", "message", StringComparison.OrdinalIgnoreCase)) return null;

        var created = m.CreatedDateTime?.UtcDateTime ?? now;
        var mentions = (m.Mentions ?? [])
            .Where(x => x.Mentioned?.User is not null)
            .Select(x => new MessageParty(null, x.Mentioned!.User!.DisplayName ?? x.MentionText, x.Mentioned.User.Id, null, "mention"))
            .GroupBy(x => x.ExternalUserId ?? x.Name)
            .Select(g => g.First())
            .ToList();
        var attachments = (m.Attachments ?? [])
            .Where(a => !string.Equals(a.ContentType, "messageReference", StringComparison.OrdinalIgnoreCase))
            .Select(a => new MessageAttachmentRef(a.Name ?? a.Id ?? "attachment", a.ContentType, 0, a.Id ?? a.ContentUrl))
            .ToList();

        return new InboundMessage
        {
            Channel = channel,
            SourceKey = sourceKey,
            ExternalId = id,
            GraphItemId = id,
            ConversationId = conversationId,
            ConversationKey = conversationKey,
            ThreadRootId = threadRootId,
            ReplyToId = m.ReplyToId,
            Subject = string.IsNullOrWhiteSpace(m.Subject) ? null : m.Subject,
            BodyText = deleted ? "" : HtmlText.TeamsBodyToText(m.Body?.Content, m.Body?.ContentType),
            FromUserId = m.From?.User?.Id,
            FromName = m.From?.User?.DisplayName ?? m.From?.Application?.DisplayName,
            Mentions = mentions,
            Attachments = attachments,
            SentAt = created,
            LastModifiedAt = m.LastModifiedDateTime?.UtcDateTime ?? m.DeletedDateTime?.UtcDateTime ?? created,
            WebUrl = m.WebUrl,
            Importance = m.Importance,
            IsDeleted = deleted,
        };
    }

    private static List<MessageParty> Parties(List<GraphRecipient>? recipients, string kind) =>
        (recipients ?? [])
        .Where(r => r.EmailAddress?.Address is { Length: > 0 })
        .Select(r => new MessageParty(r.EmailAddress!.Address, r.EmailAddress.Name, null, null, kind))
        .ToList();
}
