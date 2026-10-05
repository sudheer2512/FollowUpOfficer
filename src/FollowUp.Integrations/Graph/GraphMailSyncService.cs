using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations.Graph;

/// <summary>
/// Outlook mailbox synchronisation with Graph message delta. Source key "{mailbox}|{folder}" (inbox or sentitems).
/// Each FetchAsync returns one Graph page: ContinuationToken = @odata.nextLink while the round continues, DeltaToken =
/// @odata.deltaLink on the final page. A first sync starts at the watermark minus one hour, or InitialSyncDays back.
/// </summary>
public sealed class GraphMailSyncService : IMailSynchronizationService
{
    internal const string SelectFields =
        "id,subject,body,bodyPreview,from,toRecipients,ccRecipients,sentDateTime,receivedDateTime,lastModifiedDateTime," +
        "conversationId,internetMessageId,internetMessageHeaders,hasAttachments,importance,webLink";

    private static readonly string[] Folders = ["inbox", "sentitems"];

    private readonly GraphClient _graph;
    private readonly IOptions<GraphOptions> _options;
    private readonly Microsoft365Clock _clock;

    public GraphMailSyncService(GraphClient graph, IOptions<GraphOptions> options, Microsoft365Clock clock)
    {
        _graph = graph;
        _options = options;
        _clock = clock;
    }

    public Task<IReadOnlyList<SyncSourceInfo>> DiscoverSourcesAsync(CancellationToken ct)
    {
        var sources = new List<SyncSourceInfo>();
        foreach (var mailbox in _options.Value.ResolvedMonitoredMailboxes)
        {
            sources.Add(new SyncSourceInfo(IntegrationType.OutlookMail, $"{mailbox}|inbox", $"{mailbox} — Inbox", mailbox));
            sources.Add(new SyncSourceInfo(IntegrationType.OutlookMail, $"{mailbox}|sentitems", $"{mailbox} — Sent Items", mailbox));
        }
        return Task.FromResult<IReadOnlyList<SyncSourceInfo>>(sources);
    }

    public async Task<SyncPage> FetchAsync(SyncCursor cursor, CancellationToken ct)
    {
        var (mailbox, folder) = ParseSourceKey(cursor.SourceKey);
        var options = _options.Value;
        string url;
        if (!string.IsNullOrWhiteSpace(cursor.ContinuationToken)) url = cursor.ContinuationToken;
        else if (!string.IsNullOrWhiteSpace(cursor.DeltaToken)) url = cursor.DeltaToken;
        else
        {
            var since = cursor.Watermark is { } watermark
                ? GraphJson.AsUtc(watermark).AddHours(-1)
                : _clock.UtcNow.AddDays(-options.InitialSyncDays);
            url = $"users/{GraphJson.Segment(mailbox)}/mailFolders/{folder}/messages/delta?$select={SelectFields}"
                  + $"&$filter={Uri.EscapeDataString("receivedDateTime ge " + GraphJson.FormatDate(since))}";
        }

        KeyValuePair<string, string>[] headers =
        [
            new("Prefer", $"odata.maxpagesize={options.PageSize}"),
            new("Prefer", "IdType=\"ImmutableId\""),
        ];
        var page = await _graph.GetPageAsync<GraphMessage>(url, GraphAuth.Application, ct, headers);

        var now = _clock.UtcNow;
        var messages = new List<InboundMessage>(page.Value.Count);
        DateTime? watermarkOut = cursor.Watermark is { } w ? GraphJson.AsUtc(w) : null;
        foreach (var item in page.Value)
        {
            IReadOnlyList<MessageAttachmentRef>? attachments = null;
            if (item.Removed is null && item.HasAttachments == true && !string.IsNullOrEmpty(item.Id))
                attachments = await ListAttachmentsAsync(mailbox, item.Id, ct);
            messages.Add(GraphMessageMapper.FromMail(item, cursor.SourceKey, now, attachments));
            if (item.Removed is null && (item.ReceivedDateTime ?? item.SentDateTime) is { } received
                && (watermarkOut is null || received.UtcDateTime > watermarkOut)) watermarkOut = received.UtcDateTime;
        }

        var isFinal = string.IsNullOrEmpty(page.NextLink);
        return new SyncPage(messages, page.NextLink, isFinal ? page.DeltaLink : null, watermarkOut, isFinal);
    }

    private async Task<IReadOnlyList<MessageAttachmentRef>> ListAttachmentsAsync(string mailbox, string messageId, CancellationToken ct)
    {
        var items = await _graph.GetAllAsync<GraphAttachment>(
            $"users/{GraphJson.Segment(mailbox)}/messages/{GraphJson.Segment(messageId)}/attachments?$select=id,name,contentType,size,isInline",
            GraphAuth.Application, 100, ct);
        return items
            .Where(a => a.IsInline != true)
            .Select(a => new MessageAttachmentRef(a.Name ?? "attachment", a.ContentType, a.Size ?? 0, a.Id))
            .ToList();
    }

    /// <summary>Parses "{mailbox}|{folder}" ("/" is tolerated as separator); folder is inbox or sentitems.</summary>
    internal static (string Mailbox, string Folder) ParseSourceKey(string sourceKey)
    {
        var separator = sourceKey.LastIndexOf('|');
        if (separator < 0) separator = sourceKey.LastIndexOf('/');
        if (separator <= 0 || separator == sourceKey.Length - 1)
            throw new IntegrationException(IntegrationErrorKind.Validation, $"Invalid mail source key '{sourceKey}' (expected mailbox|folder).");
        var mailbox = sourceKey[..separator].Trim();
        var folder = sourceKey[(separator + 1)..].Trim().ToLowerInvariant();
        if (!Folders.Contains(folder))
            throw new IntegrationException(IntegrationErrorKind.Validation, $"Unsupported mail folder '{folder}' (inbox or sentitems).");
        return (mailbox, folder);
    }
}
