using FollowUp.Integrations.Graph;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Integrations.GraphEmulator;

internal sealed record MailAddress(string Address, string? Name);

internal sealed record MailHeader(string Name, string Value);

internal sealed record MailExtendedProperty(string Id, string Value);

/// <summary>A message to deliver. <see cref="SenderMailbox"/> receives the Sent Items copy (null for an external sender).</summary>
internal sealed record MailDraft(
    string? SenderMailbox,
    string FromAddress,
    string? FromName,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    string Subject,
    string BodyContentType,
    string BodyContent,
    string Importance,
    IReadOnlyList<MailHeader> Headers,
    IReadOnlyList<MailExtendedProperty> ExtendedProperties,
    DateTime SentAt,
    string? ConversationId = null,
    string? InReplyTo = null,
    bool SaveToSentItems = true);

internal sealed record DeliveredMail(string InternetMessageId, string ConversationId, string? SentItemId, IReadOnlyList<string> DeliveredTo);

/// <summary>Parameters of a delta call: a delta token, a skip token or an initial request with an optional lower time bound.</summary>
internal sealed record DeltaRequest(string? DeltaToken, string? SkipToken, DateTime? Since, int PageSize);

internal sealed record DeltaResult<T>(List<T> Items, string? SkipToken, string? DeltaToken);

internal sealed partial class GraphEmulatorStore
{
    public static readonly string[] MailFolders = ["inbox", "sentitems"];

    /// <summary>
    /// Delivers a message like Exchange: a Sent Items copy for a tenant sender and an Inbox copy for every tenant recipient.
    /// A reply (In-Reply-To matching a known Internet-Message-Id) joins the parent's conversation and extends References.
    /// </summary>
    public Task<DeliveredMail> DeliverMailAsync(MailDraft draft, CancellationToken ct) => WriteAsync(async () =>
    {
        var internetMessageId = GraphEmulatorIds.NewInternetMessageId();
        var inReplyTo = draft.InReplyTo
                        ?? draft.Headers.FirstOrDefault(h => h.Name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase))?.Value;
        EmulatorMailItem? parent = null;
        if (!string.IsNullOrWhiteSpace(inReplyTo))
            parent = await _db.MailItems.AsNoTracking().Where(m => m.InternetMessageId == inReplyTo).OrderBy(m => m.Pk).FirstOrDefaultAsync(ct);
        var conversationId = draft.ConversationId ?? parent?.ConversationId ?? GraphEmulatorIds.NewConversationId();
        var references = parent is not null
            ? $"{parent.References} {parent.InternetMessageId}".Trim()
            : draft.Headers.FirstOrDefault(h => h.Name.Equals("References", StringComparison.OrdinalIgnoreCase))?.Value;

        var headers = new List<MailHeader> { new("Message-ID", internetMessageId) };
        if (!string.IsNullOrWhiteSpace(inReplyTo)) headers.Add(new("In-Reply-To", inReplyTo));
        if (!string.IsNullOrWhiteSpace(references)) headers.Add(new("References", references));
        headers.AddRange(draft.Headers.Where(h =>
            !h.Name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase) && !h.Name.Equals("References", StringComparison.OrdinalIgnoreCase)
            && !h.Name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase)));

        var bodyText = string.Equals(draft.BodyContentType, "html", StringComparison.OrdinalIgnoreCase)
            ? HtmlText.ToPlainText(draft.BodyContent)
            : draft.BodyContent.Trim();
        var preview = bodyText.Replace('\n', ' ');
        if (preview.Length > 255) preview = preview[..255];
        var sentAt = GraphJson.AsUtc(draft.SentAt);

        EmulatorMailItem Copy(string mailbox, string folder, long seq) => new()
        {
            Id = GraphEmulatorIds.NewMailItemId(),
            Mailbox = mailbox,
            Folder = folder,
            InternetMessageId = internetMessageId,
            ConversationId = conversationId,
            Subject = draft.Subject,
            BodyContentType = draft.BodyContentType.ToLowerInvariant() == "html" ? "html" : "text",
            BodyContent = draft.BodyContent,
            BodyPreview = preview,
            FromAddress = GraphEmulatorIds.Normalize(draft.FromAddress),
            FromName = draft.FromName,
            ToJson = ToJson(draft.To),
            CcJson = ToJson(draft.Cc),
            HeadersJson = ToJson(headers),
            ExtendedPropertiesJson = ToJson(draft.ExtendedProperties),
            InReplyTo = string.IsNullOrWhiteSpace(inReplyTo) ? null : inReplyTo,
            References = references,
            Importance = NormalizeImportance(draft.Importance),
            IsRead = folder == "sentitems",
            SentDateTime = sentAt,
            ReceivedDateTime = sentAt,
            CreatedDateTime = sentAt,
            LastModifiedDateTime = sentAt,
            ChangeSeq = seq,
        };

        string? sentItemId = null;
        if (draft.SaveToSentItems && !string.IsNullOrWhiteSpace(draft.SenderMailbox))
        {
            var sender = await FindUserAsync(draft.SenderMailbox, ct);
            if (sender is not null)
            {
                var copy = Copy(sender.Mail, "sentitems", await NextSequenceAsync(ct));
                _db.MailItems.Add(copy);
                sentItemId = copy.Id;
            }
        }

        var delivered = new List<string>();
        foreach (var address in draft.To.Concat(draft.Cc).Select(r => GraphEmulatorIds.Normalize(r.Address)).Distinct())
        {
            var recipient = await FindUserAsync(address, ct);
            if (recipient is null) continue; // external recipient: leaves the tenant
            _db.MailItems.Add(Copy(recipient.Mail, "inbox", await NextSequenceAsync(ct)));
            delivered.Add(recipient.Mail);
        }

        await _db.SaveChangesAsync(ct);
        return new DeliveredMail(internetMessageId, conversationId, sentItemId, delivered);
    }, ct);

    internal static string NormalizeImportance(string? importance) => importance?.Trim().ToLowerInvariant() switch
    {
        "high" => "high",
        "low" => "low",
        _ => "normal",
    };

    public static string NormalizeFolder(string folder)
    {
        var f = folder.Trim().ToLowerInvariant();
        return f switch
        {
            "inbox" => "inbox",
            "sentitems" or "sent" or "sent items" => "sentitems",
            _ => throw GraphEmulatorException.NotFound($"mailFolders/{folder}"),
        };
    }

    /// <summary>Mail folder delta: changes with a sequence after the token, in sequence order, paged.</summary>
    public async Task<DeltaResult<EmulatorMailItem>> MailDeltaAsync(string mailbox, string folder, DeltaRequest request, CancellationToken ct)
    {
        var resource = $"mail:{mailbox}/{folder}";
        var window = await ResolveDeltaWindowAsync(resource, request, ct);
        var query = _db.MailItems.AsNoTracking()
            .Where(m => m.Mailbox == mailbox && m.Folder == folder && m.ChangeSeq > window.Lower && m.ChangeSeq <= window.UpTo);
        if (window.Initial) query = query.Where(m => !m.IsDeleted);
        if (window.Since is { } since) query = query.Where(m => m.ReceivedDateTime >= since);
        var fetched = await query.OrderBy(m => m.ChangeSeq).Take(request.PageSize + 1).ToListAsync(ct);
        return BuildDeltaResult(resource, window, fetched, m => m.ChangeSeq, request.PageSize);
    }

    /// <summary>Messages of a folder newest first (Sent Items lookups by extended property filter in memory).</summary>
    public Task<List<EmulatorMailItem>> ListMailAsync(string mailbox, string folder, DateTime? sentSince, int max, CancellationToken ct)
    {
        var query = _db.MailItems.AsNoTracking().Where(m => m.Mailbox == mailbox && m.Folder == folder && !m.IsDeleted);
        if (sentSince is { } since) query = query.Where(m => m.SentDateTime >= since);
        return query.OrderByDescending(m => m.ReceivedDateTime).ThenByDescending(m => m.Pk).Take(max).ToListAsync(ct);
    }

    public Task<EmulatorMailItem?> FindMailItemAsync(string mailbox, string id, CancellationToken ct) =>
        _db.MailItems.AsNoTracking().FirstOrDefaultAsync(m => m.Mailbox == mailbox && m.Id == id && !m.IsDeleted, ct);

    /// <summary>Finds a message by Internet-Message-Id or Graph item id (any mailbox).</summary>
    public Task<EmulatorMailItem?> FindMailByAnyIdAsync(string id, CancellationToken ct) =>
        _db.MailItems.AsNoTracking().Where(m => m.InternetMessageId == id || m.Id == id).OrderBy(m => m.Pk).FirstOrDefaultAsync(ct);

    // ── Delta plumbing (shared with channel messages) ──

    internal sealed record DeltaWindow(long Lower, long UpTo, DateTime? Since, bool Initial, int Epoch);

    private async Task<DeltaWindow> ResolveDeltaWindowAsync(string resource, DeltaRequest request, CancellationToken ct)
    {
        var settings = await SettingsSnapshotAsync(ct);
        var tokenText = request.SkipToken ?? request.DeltaToken;
        if (tokenText is null) return new DeltaWindow(0, settings.ChangeSequence, request.Since, true, settings.DeltaEpoch);

        var token = DeltaToken.TryDecode(tokenText)
                    ?? throw new GraphEmulatorException(410, "syncStateNotFound", "The sync state is not valid. Resync required.");
        if (!string.Equals(token.Resource, resource, StringComparison.OrdinalIgnoreCase))
            throw GraphEmulatorException.BadRequest("The delta token does not belong to this resource.");
        if (token.Epoch != settings.DeltaEpoch)
            throw new GraphEmulatorException(410, "syncStateNotFound", "The sync state generation was invalidated. Resync required.");
        if (RealNow - token.IssuedAt > DeltaToken.Retention)
            throw new GraphEmulatorException(410, "syncStateNotFound", "The sync state has expired. Resync required.");
        return request.SkipToken is not null
            ? new DeltaWindow(token.AfterSeq, token.UpToSeq, token.Since, token.Initial, settings.DeltaEpoch)
            : new DeltaWindow(token.FromSeq, settings.ChangeSequence, null, false, settings.DeltaEpoch);
    }

    private DeltaResult<T> BuildDeltaResult<T>(string resource, DeltaWindow window, List<T> fetched, Func<T, long> sequence, int pageSize)
    {
        var now = RealNow;
        if (fetched.Count > pageSize)
        {
            fetched.RemoveRange(pageSize, fetched.Count - pageSize);
            var skip = new DeltaToken(resource, window.Epoch, window.Lower, window.UpTo, sequence(fetched[^1]), window.Since, window.Initial, now);
            return new DeltaResult<T>(fetched, skip.Encode(), null);
        }
        var delta = new DeltaToken(resource, window.Epoch, window.UpTo, 0, 0, null, false, now);
        return new DeltaResult<T>(fetched, null, delta.Encode());
    }
}
