using System.Globalization;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations.Graph;

/// <summary>
/// Teams synchronisation. Chats (source key = chat id) have no delta query: messages modified after the watermark
/// (minus a two-minute overlap; Core dedupes) are paged newest-first and returned ascending in one final page.
/// Channels (source key "{teamId}|{channelId}") use channel message delta for root posts; the replies of every returned
/// root that changed after the watermark are fetched and returned with their thread ids.
/// </summary>
public sealed class GraphTeamsSyncService : ITeamsSynchronizationService
{
    internal static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    private const int ChatPageSize = 50;
    private const int MaxDiscoveredChats = 1_000;

    private readonly GraphClient _graph;
    private readonly GraphDirectoryService _directory;
    private readonly IOptions<GraphOptions> _options;
    private readonly Microsoft365Clock _clock;

    public GraphTeamsSyncService(GraphClient graph, GraphDirectoryService directory, IOptions<GraphOptions> options, Microsoft365Clock clock)
    {
        _graph = graph;
        _directory = directory;
        _options = options;
        _clock = clock;
    }

    public async Task<IReadOnlyList<SyncSourceInfo>> DiscoverSourcesAsync(CancellationToken ct)
    {
        var options = _options.Value;
        var sources = new List<SyncSourceInfo>();

        var account = options.ServiceAccount;
        var chats = await _graph.GetAllAsync<GraphChat>(
            $"users/{GraphJson.Segment(account)}/chats?$expand=members&$top=50", GraphAuth.Application, MaxDiscoveredChats, ct);
        foreach (var chat in chats.Where(c => !string.IsNullOrEmpty(c.Id)))
        {
            var others = (chat.Members ?? [])
                .Where(m => !string.Equals(m.Email, account, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.DisplayName ?? m.Email)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            var name = !string.IsNullOrWhiteSpace(chat.Topic) ? chat.Topic!
                : others.Count > 0 ? (chat.ChatType == "oneOnOne" ? "Chat with " : "Group chat: ") + string.Join(", ", others)
                : chat.Id!;
            sources.Add(new SyncSourceInfo(IntegrationType.TeamsChat, chat.Id!, name, account));
        }

        var configured = options.MonitoredChannels
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var teams = await _directory.ListTeamsAsync(ct);
        var known = teams
            .SelectMany(t => t.Channels.Select(c => (Key: $"{t.TeamId}|{c.ChannelId}", Name: $"{t.DisplayName} / {c.DisplayName}")))
            .ToList();
        if (configured.Count == 0)
        {
            sources.AddRange(known.Select(k => new SyncSourceInfo(IntegrationType.TeamsChannel, k.Key, k.Name, null)));
        }
        else
        {
            foreach (var key in configured)
            {
                var match = known.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
                sources.Add(new SyncSourceInfo(IntegrationType.TeamsChannel, match.Key ?? key, match.Name ?? key, null));
            }
        }
        return sources;
    }

    public Task<SyncPage> FetchAsync(SyncCursor cursor, CancellationToken ct)
    {
        var isChannel = cursor.Type == IntegrationType.TeamsChannel
                        || (cursor.Type != IntegrationType.TeamsChat && cursor.SourceKey.Contains('|'));
        return isChannel ? FetchChannelAsync(cursor, ct) : FetchChatAsync(cursor, ct);
    }

    // ── Chats ──

    private async Task<SyncPage> FetchChatAsync(SyncCursor cursor, CancellationToken ct)
    {
        var options = _options.Value;
        var chatId = cursor.SourceKey.Trim();
        if (chatId.Length == 0) throw new IntegrationException(IntegrationErrorKind.Validation, "Empty chat source key.");
        var now = _clock.UtcNow;
        DateTime? previous = cursor.Watermark is { } w ? GraphJson.AsUtc(w) : null;

        string? url;
        DateTime? carriedMax = null;
        if (!string.IsNullOrWhiteSpace(cursor.ContinuationToken))
            (carriedMax, url) = ParseChatContinuation(cursor.ContinuationToken);
        else
        {
            var since = previous is { } p ? p - Overlap : now.AddDays(-options.InitialSyncDays);
            url = $"chats/{GraphJson.Segment(chatId)}/messages?$top={ChatPageSize}"
                  + $"&$orderby={Uri.EscapeDataString("lastModifiedDateTime desc")}"
                  + $"&$filter={Uri.EscapeDataString("lastModifiedDateTime gt " + GraphJson.FormatDate(since))}";
        }

        var raw = new List<GraphChatMessage>();
        while (url is not null && raw.Count < options.MaxChatMessagesPerFetch)
        {
            var page = await _graph.GetPageAsync<GraphChatMessage>(url, GraphAuth.Application, ct);
            raw.AddRange(page.Value);
            url = page.NextLink;
        }

        var maxSeen = Max(carriedMax, raw.Select(m => m.LastModifiedDateTime?.UtcDateTime ?? m.CreatedDateTime?.UtcDateTime));
        var messages = raw
            .Select(m => GraphMessageMapper.FromTeams(m, cursor.SourceKey, CommunicationChannel.TeamsChat, chatId, chatId, null, now))
            .OfType<InboundMessage>()
            .OrderBy(m => m.LastModifiedAt).ThenBy(m => m.SentAt).ThenBy(m => m.ExternalId, StringComparer.Ordinal)
            .ToList();

        if (url is not null)
        {
            // More (older) messages remain: keep the watermark where it was and carry the newest timestamp seen so far.
            var token = $"{(maxSeen ?? previous ?? now).Ticks.ToString(CultureInfo.InvariantCulture)}|{url}";
            return new SyncPage(messages, token, null, previous, false);
        }
        return new SyncPage(messages, null, null, Max(previous, [maxSeen]), true);
    }

    private static (DateTime? CarriedMax, string Url) ParseChatContinuation(string token)
    {
        var separator = token.IndexOf('|');
        if (separator > 0 && long.TryParse(token.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            return (new DateTime(ticks, DateTimeKind.Utc), token[(separator + 1)..]);
        return (null, token);
    }

    // ── Channels ──

    private async Task<SyncPage> FetchChannelAsync(SyncCursor cursor, CancellationToken ct)
    {
        var options = _options.Value;
        var (teamId, channelId) = ParseChannelKey(cursor.SourceKey);
        var conversationKey = $"{teamId}|{channelId}";
        var now = _clock.UtcNow;
        DateTime? previous = cursor.Watermark is { } w ? GraphJson.AsUtc(w) : null;
        var since = previous is { } p ? p - Overlap : now.AddDays(-options.InitialSyncDays);
        var channelPath = $"teams/{GraphJson.Segment(teamId)}/channels/{GraphJson.Segment(channelId)}/messages";

        string url;
        if (!string.IsNullOrWhiteSpace(cursor.ContinuationToken)) url = cursor.ContinuationToken;
        else if (!string.IsNullOrWhiteSpace(cursor.DeltaToken)) url = cursor.DeltaToken;
        else
            url = $"{channelPath}/delta?$top={Math.Min(options.PageSize, 50)}"
                  + $"&$filter={Uri.EscapeDataString("lastModifiedDateTime gt " + GraphJson.FormatDate(since))}";

        var page = await _graph.GetPageAsync<GraphChatMessage>(url, GraphAuth.Application, ct);
        var messages = new List<InboundMessage>();
        var timestamps = new List<DateTime?>();
        foreach (var root in page.Value)
        {
            if (string.IsNullOrEmpty(root.Id)) continue;
            var rootConversation = $"{conversationKey}|{root.Id}";
            var mappedRoot = GraphMessageMapper.FromTeams(root, cursor.SourceKey, CommunicationChannel.TeamsChannel, conversationKey,
                rootConversation, null, now);
            if (mappedRoot is null) continue; // system / event message
            messages.Add(mappedRoot);
            timestamps.Add(mappedRoot.LastModifiedAt);
            if (mappedRoot.IsDeleted) continue;

            var replies = await _graph.GetAllAsync<GraphChatMessage>(
                $"{channelPath}/{GraphJson.Segment(root.Id)}/replies?$top=50", GraphAuth.Application, options.MaxRepliesPerThread, ct);
            foreach (var reply in replies
                         .Where(r => (r.LastModifiedDateTime ?? r.CreatedDateTime)?.UtcDateTime > since)
                         .OrderBy(r => r.CreatedDateTime))
            {
                var mapped = GraphMessageMapper.FromTeams(reply, cursor.SourceKey, CommunicationChannel.TeamsChannel, conversationKey,
                    rootConversation, root.Id, now);
                if (mapped is null) continue;
                messages.Add(mapped with { ReplyToId = mapped.ReplyToId ?? root.Id });
                timestamps.Add(mapped.LastModifiedAt);
            }
        }

        var isFinal = string.IsNullOrEmpty(page.NextLink);
        // Intermediate pages keep the incoming watermark so replies on later pages are still filtered from the same point.
        var watermark = isFinal ? Max(previous, timestamps) : previous;
        return new SyncPage(messages, page.NextLink, isFinal ? page.DeltaLink : null, watermark, isFinal);
    }

    /// <summary>Parses "{teamId}|{channelId}" ("/" is tolerated as separator).</summary>
    internal static (string TeamId, string ChannelId) ParseChannelKey(string sourceKey)
    {
        var separator = sourceKey.IndexOf('|');
        if (separator < 0) separator = sourceKey.IndexOf('/');
        if (separator <= 0 || separator == sourceKey.Length - 1)
            throw new IntegrationException(IntegrationErrorKind.Validation, $"Invalid channel source key '{sourceKey}' (expected teamId|channelId).");
        return (sourceKey[..separator].Trim(), sourceKey[(separator + 1)..].Trim());
    }

    private static DateTime? Max(DateTime? seed, IEnumerable<DateTime?> values)
    {
        var max = seed;
        foreach (var v in values)
            if (v is { } value && (max is null || value > max)) max = value;
        return max;
    }
}
