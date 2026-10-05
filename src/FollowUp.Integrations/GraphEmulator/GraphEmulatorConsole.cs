using System.Globalization;
using System.Net;
using FollowUp.Core.Abstractions;
using FollowUp.Integrations.Graph;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations.GraphEmulator;

/// <summary>
/// Console API of the "Microsoft 365 Simulator" page: browse mailboxes, chats, channels and the activity feed of the
/// emulated tenant, act as any user (mail, chat, channel posts), inject faults and invalidate delta tokens.
/// The host mounts it on an authorised group (e.g. "/api/m365-sim").
/// </summary>
public static class GraphEmulatorConsole
{
    /// <summary>Maps the console endpoints on <paramref name="group"/>; outside Emulator mode every route answers 404.</summary>
    public static RouteGroupBuilder MapGraphEmulatorConsole(this RouteGroupBuilder group)
    {
        var options = ((IEndpointRouteBuilder)group).ServiceProvider.GetRequiredService<IOptions<GraphOptions>>().Value;
        if (options.Mode != Microsoft365Mode.Emulator)
        {
            group.Map("/{**path}", () => Results.Problem(statusCode: 404, title: "Microsoft 365 simulator unavailable",
                detail: "The simulator is available only when Microsoft365:Mode is Emulator."));
            return group;
        }

        var sim = group.MapGroup("").AddEndpointFilter(ErrorFilterAsync);
        sim.MapGet("/users", ListUsersAsync);
        sim.MapGet("/mailboxes/{address}/messages", ListMailboxAsync);
        sim.MapGet("/chats", ListChatsAsync);
        sim.MapPost("/chats/one-on-one", EnsureOneOnOneAsync);
        sim.MapGet("/chats/{chatId}/messages", ListChatMessagesAsync);
        sim.MapPost("/chats/{chatId}/messages", PostChatMessageAsync);
        sim.MapGet("/channels", ListChannelsAsync);
        sim.MapGet("/teams/{teamId}/channels/{channelId}/messages", ListChannelMessagesAsync);
        sim.MapPost("/teams/{teamId}/channels/{channelId}/messages", PostChannelMessageAsync);
        sim.MapGet("/activity", ListActivityAsync);
        sim.MapGet("/faults", GetFaultsAsync);
        sim.MapPut("/faults", SetFaultsAsync);
        sim.MapPost("/mail", ComposeMailAsync);
        sim.MapPost("/delta/invalidate", InvalidateDeltaAsync);
        return group;
    }

    private static async ValueTask<object?> ErrorFilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (GraphEmulatorException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode, title: ex.Code, detail: ex.Message);
        }
    }

    // ── Directory ──

    private static async Task<IResult> ListUsersAsync(GraphEmulatorStore store, CancellationToken ct)
    {
        var users = await store.ListUsersAsync(ct);
        return Results.Ok(users.Select(u => new SimUserDto(u.Id, u.DisplayName, u.Mail, u.JobTitle, u.Department, u.ManagerId)).ToList());
    }

    // ── Mail ──

    private static async Task<IResult> ListMailboxAsync(string address, string? folder, int? top, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(address, ct);
        var normalized = GraphEmulatorStore.NormalizeFolder(folder ?? "inbox");
        var items = await store.ListMailAsync(user.Mail, normalized, null, Math.Clamp(top ?? 100, 1, 1000), ct);
        return Results.Ok(items.Select(m => ToDto(m)).ToList());
    }

    private static async Task<IResult> ComposeMailAsync(SimComposeMailRequest request, GraphEmulatorStore store, CancellationToken ct)
    {
        var from = await store.RequireUserAsync(request.From, ct);
        var to = await AddressesAsync(store, request.To, ct);
        var cc = await AddressesAsync(store, request.Cc ?? [], ct);
        if (to.Count + cc.Count == 0) throw GraphEmulatorException.BadRequest("At least one recipient is required.");

        var subject = request.Subject?.Trim() ?? "";
        var html = GraphEmulatorTenant.TextToMailHtml(request.Body);
        string? inReplyTo = null;
        if (!string.IsNullOrWhiteSpace(request.InReplyToMessageId))
        {
            var parent = await store.FindMailByAnyIdAsync(request.InReplyToMessageId.Trim(), ct)
                         ?? throw GraphEmulatorException.NotFound(request.InReplyToMessageId);
            inReplyTo = parent.InternetMessageId;
            if (subject.Length == 0)
                subject = parent.Subject.StartsWith("RE:", StringComparison.OrdinalIgnoreCase) ? parent.Subject : "RE: " + parent.Subject;
            if (request.QuoteOriginal) html = WithQuotedOriginal(request.Body, parent);
        }
        if (subject.Length == 0) throw GraphEmulatorException.BadRequest("A subject is required.");

        var delivered = await store.DeliverMailAsync(new MailDraft(from.Mail, from.Mail, from.DisplayName, to, cc, subject, "html", html,
            request.HighImportance ? "high" : "normal", [], [], store.Now, InReplyTo: inReplyTo), ct);
        return Results.Ok(new SimMailSentDto(delivered.InternetMessageId, delivered.ConversationId, delivered.DeliveredTo));
    }

    /// <summary>Outlook-style reply body: the new text, then the quoted original under a "From/Sent/To/Subject" header block.</summary>
    private static string WithQuotedOriginal(string reply, EmulatorMailItem parent)
    {
        var to = string.Join("; ", GraphEmulatorStore.FromJson<MailAddress>(parent.ToJson).Select(a => a.Name ?? a.Address));
        var original = parent.BodyContentType == "html" ? HtmlText.ToPlainText(parent.BodyContent) : parent.BodyContent;
        var originalHtml = string.Join("", original.Split('\n').Select(l => $"<div>{(l.Length == 0 ? "<br>" : WebUtility.HtmlEncode(l))}</div>"));
        var replyHtml = string.Join("", reply.Replace("\r\n", "\n").Split('\n').Select(l => $"<div>{(l.Length == 0 ? "<br>" : WebUtility.HtmlEncode(l))}</div>"));
        return "<html><body>" + replyHtml
               + "<hr style=\"display:inline-block;width:98%\" tabindex=\"-1\">"
               + "<div id=\"divRplyFwdMsg\" dir=\"ltr\"><font face=\"Calibri, sans-serif\" style=\"font-size:11pt\" color=\"#000000\">"
               + $"<b>From:</b> {WebUtility.HtmlEncode(parent.FromName ?? parent.FromAddress)} &lt;{WebUtility.HtmlEncode(parent.FromAddress)}&gt;<br>"
               + $"<b>Sent:</b> {parent.SentDateTime.ToString("dddd, MMMM d, yyyy h:mm tt", CultureInfo.InvariantCulture)}<br>"
               + $"<b>To:</b> {WebUtility.HtmlEncode(to)}<br>"
               + $"<b>Subject:</b> {WebUtility.HtmlEncode(parent.Subject)}</font><div>&nbsp;</div></div>"
               + originalHtml + "</body></html>";
    }

    private static async Task<List<MailAddress>> AddressesAsync(GraphEmulatorStore store, IReadOnlyList<string> values, CancellationToken ct)
    {
        var result = new List<MailAddress>();
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            var user = await store.FindUserAsync(value, ct);
            if (user is not null) result.Add(new MailAddress(user.Mail, user.DisplayName));
            else if (value.Contains('@')) result.Add(new MailAddress(value.Trim(), null));
            else throw GraphEmulatorException.NotFound(value);
        }
        return result;
    }

    internal static SimMailDto ToDto(EmulatorMailItem m)
    {
        var headers = GraphEmulatorStore.FromJson<MailHeader>(m.HeadersJson)
            .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        var key = headers.TryGetValue(GraphEmailService.FollowUpKeyHeader, out var header)
            ? header
            : GraphEmulatorStore.FromJson<MailExtendedProperty>(m.ExtendedPropertiesJson)
                .FirstOrDefault(p => p.Id.Equals(GraphEmailService.FollowUpKeyPropertyId, StringComparison.OrdinalIgnoreCase))?.Value;
        var text = m.BodyContentType == "html" ? HtmlText.ToPlainText(m.BodyContent) : m.BodyContent.Trim();
        return new SimMailDto(m.Id, m.InternetMessageId, m.ConversationId, m.Mailbox, m.Folder, m.Subject, text, m.BodyPreview, m.FromAddress,
            m.FromName,
            GraphEmulatorStore.FromJson<MailAddress>(m.ToJson).Select(a => new SimRecipientDto(a.Address, a.Name)).ToList(),
            GraphEmulatorStore.FromJson<MailAddress>(m.CcJson).Select(a => new SimRecipientDto(a.Address, a.Name)).ToList(),
            m.SentDateTime, m.ReceivedDateTime, m.Importance, m.InReplyTo, key, headers);
    }

    // ── Chats ──

    private static async Task<IResult> ListChatsAsync(string? userId, GraphEmulatorStore store, CancellationToken ct)
    {
        string? memberId = null;
        if (!string.IsNullOrWhiteSpace(userId)) memberId = (await store.RequireUserAsync(userId, ct)).Id;
        var chats = await store.ListChatsAsync(memberId, ct);
        return Results.Ok(await ChatDtosAsync(store, chats, ct));
    }

    private static async Task<List<SimChatDto>> ChatDtosAsync(GraphEmulatorStore store, List<EmulatorChat> chats, CancellationToken ct)
    {
        var ids = chats.Select(c => c.Id).ToList();
        var members = (await store.ChatMembersAsync(ids, ct)).ToLookup(m => m.ChatId);
        var users = await store.UserMapAsync(ct);
        var lastIds = await store.Db.TeamsMessages.AsNoTracking()
            .Where(m => m.ChatId != null && ids.Contains(m.ChatId))
            .GroupBy(m => m.ChatId!)
            .Select(g => g.Max(m => m.Pk))
            .ToListAsync(ct);
        var last = await store.Db.TeamsMessages.AsNoTracking().Where(m => lastIds.Contains(m.Pk)).ToDictionaryAsync(m => m.ChatId!, ct);

        return chats.Select(c =>
        {
            var memberDtos = members[c.Id]
                .Select(m => users.TryGetValue(m.UserId, out var u)
                    ? new SimChatMemberDto(u.Id, u.DisplayName, u.Mail)
                    : new SimChatMemberDto(m.UserId, m.UserId, null))
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var name = !string.IsNullOrWhiteSpace(c.Topic) ? c.Topic! : string.Join(", ", memberDtos.Select(m => m.DisplayName));
            return new SimChatDto(c.Id, c.ChatType, c.Topic, name, memberDtos, last.TryGetValue(c.Id, out var lm) ? ToDto(lm, []) : null, c.LastUpdatedAt);
        }).ToList();
    }

    private static async Task<IResult> EnsureOneOnOneAsync(SimOneOnOneRequest request, GraphEmulatorStore store, CancellationToken ct)
    {
        var a = await store.RequireUserAsync(request.UserA, ct);
        var b = await store.RequireUserAsync(request.UserB, ct);
        var (chat, _) = await store.EnsureOneOnOneChatAsync(a.Id, b.Id, ct);
        return Results.Ok((await ChatDtosAsync(store, [chat], ct))[0]);
    }

    private static async Task<IResult> ListChatMessagesAsync(string chatId, int? top, GraphEmulatorStore store, CancellationToken ct)
    {
        await store.RequireChatAsync(chatId, ct);
        var (items, _) = await store.ListTeamsMessagesAsync(chatId, false, null,
            new TeamsMessageQuery(Math.Clamp(top ?? 200, 1, 1000), false, null, null, null), ct);
        return Results.Ok(items.OrderBy(m => m.CreatedDateTime).ThenBy(m => m.Pk).Select(m => ToDto(m, [])).ToList());
    }

    private static async Task<IResult> PostChatMessageAsync(string chatId, SimChatPostRequest request, GraphEmulatorStore store,
        GraphEmulatorTenant tenant, CancellationToken ct)
    {
        var draft = await tenant.DraftAsync(new TenantPost(request.FromUserId, request.Text, store.Now, null, request.MentionUserIds), null, ct,
            request.Urgent ? "urgent" : "normal");
        var message = await store.PostChatMessageAsync(chatId, draft, ct);
        return Results.Created((string?)null, new SimPostResultDto(message.MessageId));
    }

    // ── Channels ──

    private static async Task<IResult> ListChannelsAsync(GraphEmulatorStore store, CancellationToken ct)
    {
        var teams = (await store.ListTeamsAsync(ct)).ToDictionary(t => t.Id);
        var channels = await store.ListChannelsAsync(null, ct);
        var stats = await store.Db.TeamsMessages.AsNoTracking()
            .Where(m => m.TeamId != null)
            .GroupBy(m => m.ContainerId)
            .Select(g => new { g.Key, Count = g.Count(), Last = g.Max(m => m.LastModifiedDateTime) })
            .ToDictionaryAsync(x => x.Key, ct);
        return Results.Ok(channels
            .Select(c =>
            {
                var s = stats.TryGetValue(GraphEmulatorStore.ChannelContainer(c.TeamId, c.Id), out var v) ? v : null;
                return new SimChannelDto(c.TeamId, teams.TryGetValue(c.TeamId, out var t) ? t.DisplayName : c.TeamId, c.Id, c.DisplayName,
                    s?.Count ?? 0, s is null ? null : DateTime.SpecifyKind(s.Last, DateTimeKind.Utc));
            })
            .OrderBy(c => c.TeamName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.ChannelName, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static async Task<IResult> ListChannelMessagesAsync(string teamId, string channelId, int? top, GraphEmulatorStore store,
        CancellationToken ct)
    {
        await store.RequireChannelAsync(teamId, channelId, ct);
        var container = GraphEmulatorStore.ChannelContainer(teamId, channelId);
        var (roots, _) = await store.ListTeamsMessagesAsync(container, true, null,
            new TeamsMessageQuery(Math.Clamp(top ?? 100, 1, 1000), false, null, null, null), ct);
        var replies = (await store.RepliesAsync(container, roots.Select(r => r.MessageId).ToList(), ct)).ToLookup(r => r.ReplyToId!);
        return Results.Ok(roots
            .OrderBy(r => r.CreatedDateTime).ThenBy(r => r.Pk)
            .Select(r => ToDto(r, replies[r.MessageId].Select(x => ToDto(x, [])).ToList()))
            .ToList());
    }

    private static async Task<IResult> PostChannelMessageAsync(string teamId, string channelId, SimChannelPostRequest request,
        GraphEmulatorStore store, GraphEmulatorTenant tenant, CancellationToken ct)
    {
        var draft = await tenant.DraftAsync(new TenantPost(request.FromUserId, request.Text, store.Now, request.ReplyToId, request.MentionUserIds),
            request.ReplyToId, ct, request.Urgent ? "urgent" : "normal", request.Subject);
        var message = await store.PostChannelMessageAsync(teamId, channelId, draft, ct);
        return Results.Created((string?)null, new SimPostResultDto(message.MessageId));
    }

    internal static SimTeamsMessageDto ToDto(EmulatorTeamsMessage m, IReadOnlyList<SimTeamsMessageDto> replies) => new(
        m.MessageId, m.ReplyToId, m.FromUserId, m.FromDisplayName, m.Subject,
        m.DeletedDateTime is null ? m.BodyContent : "",
        m.DeletedDateTime is null ? HtmlText.TeamsBodyToText(m.BodyContent, m.BodyContentType) : "",
        m.Importance, m.CreatedDateTime, m.LastModifiedDateTime, m.DeletedDateTime is not null,
        GraphEmulatorStore.FromJson<StoredMention>(m.MentionsJson).Select(x => new SimMentionDto(x.UserId, x.DisplayName)).ToList(),
        replies);

    // ── Activity, faults, delta ──

    private static async Task<IResult> ListActivityAsync(string? userId, int? top, GraphEmulatorStore store, CancellationToken ct)
    {
        string? memberId = null;
        if (!string.IsNullOrWhiteSpace(userId)) memberId = (await store.RequireUserAsync(userId, ct)).Id;
        var items = await store.ListActivityAsync(memberId, Math.Clamp(top ?? 100, 1, 1000), ct);
        var users = await store.UserMapAsync(ct);
        return Results.Ok(items.Select(a =>
        {
            var text = GraphEmulatorStore.FromJson<MailHeader>(a.TemplateParametersJson)
                .FirstOrDefault(p => p.Name.Equals("systemDefaultText", StringComparison.OrdinalIgnoreCase))?.Value;
            return new SimActivityDto(a.Id, a.UserId, users.TryGetValue(a.UserId, out var u) ? u.DisplayName : null, a.ActivityType, a.TopicValue,
                a.PreviewText, text, a.CreatedDateTime);
        }).ToList());
    }

    private static async Task<IResult> GetFaultsAsync(GraphEmulatorStore store, CancellationToken ct)
    {
        var faults = await store.GetFaultsAsync(ct);
        return Results.Ok(new SimFaultsDto(faults.Outage, faults.ThrottlePercent, faults.AuthFailure, faults.LatencyMs, await store.GetDeltaEpochAsync(ct)));
    }

    private static async Task<IResult> SetFaultsAsync(SimFaultsRequest request, GraphEmulatorStore store, CancellationToken ct)
    {
        if (request.ThrottlePercent is < 0 or > 100) throw GraphEmulatorException.BadRequest("ThrottlePercent must be between 0 and 100.");
        if (request.LatencyMs is < 0 or > 60_000) throw GraphEmulatorException.BadRequest("LatencyMs must be between 0 and 60000.");
        var applied = await store.SetFaultsAsync(new TenantFaults(request.Outage, request.ThrottlePercent, request.AuthFailure, request.LatencyMs), ct);
        return Results.Ok(new SimFaultsDto(applied.Outage, applied.ThrottlePercent, applied.AuthFailure, applied.LatencyMs, await store.GetDeltaEpochAsync(ct)));
    }

    private static async Task<IResult> InvalidateDeltaAsync(GraphEmulatorStore store, CancellationToken ct) =>
        Results.Ok(new SimDeltaInvalidatedDto(await store.InvalidateDeltaTokensAsync(ct)));
}
