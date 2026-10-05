using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FollowUp.Integrations.Graph;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations.GraphEmulator;

/// <summary>
/// Graph-compatible REST endpoints under "/graph-emulator": the OAuth 2.0 token endpoint and the subset of Graph v1.0
/// the FollowUp Graph services call, with real Graph JSON shapes, paging and delta links. Every v1.0 call requires a
/// bearer token issued by the emulator and passes through fault injection (outage, throttling, auth failure, latency).
/// </summary>
public static partial class GraphEmulatorEndpoints
{
    private const string PrincipalKey = "graph-emulator.principal";

    /// <summary>Maps the emulator (only when Microsoft365:Mode is Emulator; otherwise nothing is mapped).</summary>
    public static IEndpointRouteBuilder MapGraphEmulator(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<GraphOptions>>().Value;
        if (options.Mode != Microsoft365Mode.Emulator) return app;

        var root = app.MapGroup(GraphOptions.EmulatorPathPrefix).AllowAnonymous().ExcludeFromDescription();
        root.MapPost("/{tenant}/oauth2/v2.0/token", IssueTokenAsync);

        var v1 = root.MapGroup("/v1.0").AddEndpointFilter(GateAsync);

        // Directory
        v1.MapGet("/users", ListUsersAsync);
        v1.MapGet("/users/{userId}", GetUserAsync);
        v1.MapGet("/groups", ListGroupsAsync);
        v1.MapGet("/teams/{teamId}/channels", ListChannelsAsync);
        v1.MapGet("/teams/{teamId}/channels/{channelId}", GetChannelAsync);

        // Chats
        v1.MapGet("/users/{userId}/chats", ListUserChatsAsync);
        v1.MapPost("/chats", CreateChatAsync);
        v1.MapGet("/chats/{chatId}", GetChatAsync);
        v1.MapGet("/chats/{chatId}/messages", ListChatMessagesAsync);
        v1.MapGet("/chats/{chatId}/messages/{messageId}", GetChatMessageAsync);
        v1.MapPost("/chats/{chatId}/messages", PostChatMessageAsync);

        // Channels
        v1.MapGet("/teams/{teamId}/channels/{channelId}/messages", ListChannelMessagesAsync);
        v1.MapPost("/teams/{teamId}/channels/{channelId}/messages", PostChannelMessageAsync);
        v1.MapGet("/teams/{teamId}/channels/{channelId}/messages/delta", ChannelDeltaAsync);
        v1.MapGet("/teams/{teamId}/channels/{channelId}/messages/{messageId}", GetChannelMessageAsync);
        v1.MapGet("/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies", ListRepliesAsync);
        v1.MapPost("/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies", PostReplyAsync);

        // Mail
        v1.MapGet("/users/{userId}/mailFolders/{folder}/messages/delta", MailDeltaAsync);
        v1.MapGet("/users/{userId}/mailFolders/{folder}/messages", ListFolderMessagesAsync);
        v1.MapGet("/users/{userId}/messages/{messageId}", GetMessageAsync);
        v1.MapGet("/users/{userId}/messages/{messageId}/attachments", ListAttachmentsAsync);
        v1.MapPost("/users/{userId}/sendMail", SendMailAsync);

        // Calendar
        v1.MapGet("/users/{userId}/events", ListEventsAsync);
        v1.MapPost("/users/{userId}/events", CreateEventAsync);
        v1.MapGet("/users/{userId}/events/{eventId}", GetEventAsync);
        v1.MapPatch("/users/{userId}/events/{eventId}", UpdateEventAsync);
        v1.MapDelete("/users/{userId}/events/{eventId}", DeleteEventAsync);

        // Activity feed
        v1.MapPost("/users/{userId}/teamwork/sendActivityNotification", SendActivityAsync);

        v1.Map("/{**path}", (string? path) =>
            Error(400, "BadRequest", $"Resource not found for the segment '{path}' (not supported by the Graph emulator)."));
        return app;
    }

    // ═════════════════════════════ Token endpoint ═════════════════════════════

    private static async Task<IResult> IssueTokenAsync(string tenant, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
            return OAuthError(400, "invalid_request", "AADSTS900144: The request body must contain the 'grant_type' form parameter.");
        var form = await http.Request.ReadFormAsync(ct);
        var faults = await store.GetFaultsAsync(ct);
        if (faults.AuthFailure)
            return OAuthError(401, "invalid_client", "AADSTS7000215: Invalid client secret provided (emulated authentication failure).");

        var options = store.Options;
        if (!string.Equals(tenant, options.TenantId, StringComparison.OrdinalIgnoreCase))
            return OAuthError(400, "invalid_request", $"AADSTS90002: Tenant '{tenant}' not found.");
        var clientId = form["client_id"].ToString();
        if (!string.Equals(clientId, options.ClientId, StringComparison.Ordinal))
            return OAuthError(400, "unauthorized_client", $"AADSTS700016: Application with identifier '{clientId}' was not found in the directory.");
        var secret = form["client_secret"].ToString();

        switch (form["grant_type"].ToString())
        {
            case "client_credentials":
            {
                if (!string.Equals(secret, options.ClientSecret, StringComparison.Ordinal))
                    return OAuthError(401, "invalid_client", "AADSTS7000215: Invalid client secret provided.");
                var issued = await store.IssueTokenAsync(clientId, null, ct);
                return TokenResult(issued);
            }
            case "refresh_token":
            {
                if (secret.Length > 0 && !string.Equals(secret, options.ClientSecret, StringComparison.Ordinal))
                    return OAuthError(401, "invalid_client", "AADSTS7000215: Invalid client secret provided.");
                var user = await store.ResolveRefreshTokenAsync(form["refresh_token"].ToString(), ct);
                if (user is null)
                    return OAuthError(400, "invalid_grant", "AADSTS70000: The provided refresh token is invalid or has expired.");
                var issued = await store.IssueTokenAsync(clientId, user, ct);
                return TokenResult(issued);
            }
            default:
                return OAuthError(400, "unsupported_grant_type", "AADSTS70003: The app requested an unsupported grant type.");
        }
    }

    private static IResult TokenResult(GraphEmulatorStore.IssuedTokenSet issued) => Results.Json(new TokenEndpointResponse
    {
        TokenType = "Bearer",
        ExpiresIn = issued.ExpiresIn,
        ExtExpiresIn = issued.ExpiresIn,
        AccessToken = issued.AccessToken,
        RefreshToken = issued.RefreshToken,
        Scope = issued.Scope,
    }, GraphJson.Options);

    private static IResult OAuthError(int status, string error, string description) => Results.Json(new TokenEndpointResponse
    {
        Error = error,
        ErrorDescription = $"{description}\r\nTrace ID: {Guid.NewGuid()}\r\nTimestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z",
    }, GraphJson.Options, statusCode: status);

    // ═════════════════════════════ Gate: faults + authentication + errors ═════════════════════════════

    private static async ValueTask<object?> GateAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        var requestId = Guid.NewGuid().ToString();
        http.Response.Headers["request-id"] = requestId;
        http.Response.Headers["client-request-id"] = requestId;
        try
        {
            var store = http.RequestServices.GetRequiredService<GraphEmulatorStore>();
            var faults = await store.GetFaultsAsync(ct);
            if (faults.LatencyMs > 0) await Task.Delay(faults.LatencyMs, ct);
            if (faults.Outage)
                return Error(503, "ServiceUnavailable", "The service is currently unavailable (emulated outage).");
            if (faults.AuthFailure)
                return Error(401, "InvalidAuthenticationToken", "Access token validation failure (emulated authentication failure).");

            var header = http.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || header.Length <= 7)
                return Error(401, "InvalidAuthenticationToken", "Access token is empty.");
            var principal = await store.ValidateAccessTokenAsync(header[7..].Trim(), ct);
            if (principal is null)
                return Error(401, "InvalidAuthenticationToken", "Access token has expired or is not yet valid.");
            http.Items[PrincipalKey] = principal;

            if (faults.ThrottlePercent > 0 && Random.Shared.Next(100) < faults.ThrottlePercent)
            {
                http.Response.Headers.RetryAfter = "1";
                return Error(429, "TooManyRequests", "Too many requests (emulated throttling). Retry after 1 second.");
            }
            return await next(context);
        }
        catch (GraphEmulatorException ex)
        {
            return Error(ex.StatusCode, ex.Code, ex.Message);
        }
        catch (JsonException ex)
        {
            return Error(400, "BadRequest", "Unable to read JSON request payload: " + ex.Message);
        }
    }

    internal static IResult Error(int status, string code, string message) => Results.Json(new GraphErrorResponse
    {
        Error = new GraphErrorDetail
        {
            Code = code,
            Message = message,
            InnerError = new GraphInnerError { Date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), RequestId = Guid.NewGuid().ToString() },
        },
    }, GraphJson.Options, statusCode: status);

    private static EmulatorPrincipal Principal(HttpContext http) =>
        http.Items[PrincipalKey] as EmulatorPrincipal ?? throw new GraphEmulatorException(401, "InvalidAuthenticationToken", "Not authenticated.");

    private static EmulatorPrincipal RequireDelegated(HttpContext http, string what)
    {
        var principal = Principal(http);
        if (!principal.IsDelegated || principal.UserId is null)
            throw GraphEmulatorException.Forbidden($"{what} is not supported in application-only context (delegated permissions are required).");
        return principal;
    }

    // ═════════════════════════════ Directory ═════════════════════════════

    private static async Task<IResult> ListUsersAsync(HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var expandManager = Query(http, "$expand").Contains("manager", StringComparison.OrdinalIgnoreCase);
        var users = await store.ListUsersAsync(ct);
        return OffsetPage(http, users, 100, 999, u => GraphEmulatorMapper.User(u, expandManager));
    }

    private static async Task<IResult> GetUserAsync(string userId, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        return Results.Json(GraphEmulatorMapper.User(user, Query(http, "$expand").Contains("manager", StringComparison.OrdinalIgnoreCase)), GraphJson.Options);
    }

    private static async Task<IResult> ListGroupsAsync(HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        // Only Teams-provisioned groups exist in the emulator, so the resourceProvisioningOptions filter is implicit.
        var teams = await store.ListTeamsAsync(ct);
        return OffsetPage(http, teams, 100, 999, GraphEmulatorMapper.Group);
    }

    private static async Task<IResult> ListChannelsAsync(string teamId, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        await store.RequireTeamAsync(teamId, ct);
        var channels = await store.ListChannelsAsync(teamId, ct);
        return Collection(channels.Select(GraphEmulatorMapper.Channel).ToList(), null, null);
    }

    private static async Task<IResult> GetChannelAsync(string teamId, string channelId, GraphEmulatorStore store, CancellationToken ct) =>
        Results.Json(GraphEmulatorMapper.Channel(await store.RequireChannelAsync(teamId, channelId, ct)), GraphJson.Options);

    // ═════════════════════════════ Chats ═════════════════════════════

    private static async Task<IResult> ListUserChatsAsync(string userId, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        var chats = await store.ListChatsAsync(user.Id, ct);
        var expandMembers = Query(http, "$expand").Contains("members", StringComparison.OrdinalIgnoreCase);
        var members = expandMembers ? (await store.ChatMembersAsync(chats.Select(c => c.Id).ToList(), ct)).ToLookup(m => m.ChatId) : null;
        var users = expandMembers ? await store.UserMapAsync(ct) : null;
        var tenant = store.Options.TenantId ?? "";
        return OffsetPage(http, chats, 50, 50, c => GraphEmulatorMapper.Chat(c, tenant, members?[c.Id], users));
    }

    private static async Task<IResult> GetChatAsync(string chatId, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var chat = await store.RequireChatAsync(chatId, ct);
        var members = await store.ChatMembersAsync([chatId], ct);
        return Results.Json(GraphEmulatorMapper.Chat(chat, store.Options.TenantId ?? "", members, await store.UserMapAsync(ct)), GraphJson.Options);
    }

    private static async Task<IResult> CreateChatAsync(HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct) as JsonObject
                   ?? throw GraphEmulatorException.BadRequest("A chat body is required.");
        var chatType = body["chatType"]?.GetValue<string>() ?? "group";
        var memberIds = new List<string>();
        foreach (var member in body["members"] as JsonArray ?? new JsonArray())
        {
            var reference = member?["user@odata.bind"]?.GetValue<string>() ?? member?["userId"]?.GetValue<string>()
                            ?? throw GraphEmulatorException.BadRequest("Every member needs 'user@odata.bind'.");
            var key = UserBindRegex().Match(reference) is { Success: true } m ? m.Groups["id"].Value.Replace("''", "'") : reference;
            memberIds.Add((await store.RequireUserAsync(key, ct)).Id);
        }
        memberIds = memberIds.Distinct().ToList();

        var principal = Principal(http);
        if (principal.IsDelegated && !memberIds.Contains(principal.UserId!))
            throw GraphEmulatorException.Forbidden("The signed-in user must be a member of the chat being created.");

        EmulatorChat chat;
        var created = true;
        if (string.Equals(chatType, "oneOnOne", StringComparison.OrdinalIgnoreCase))
        {
            if (memberIds.Count != 2) throw GraphEmulatorException.BadRequest("A oneOnOne chat must have exactly two members.");
            (chat, created) = await store.EnsureOneOnOneChatAsync(memberIds[0], memberIds[1], ct);
        }
        else
        {
            chat = await store.CreateGroupChatAsync(body["topic"]?.GetValue<string>(), memberIds, ct);
        }
        var members = await store.ChatMembersAsync([chat.Id], ct);
        var dto = GraphEmulatorMapper.Chat(chat, store.Options.TenantId ?? "", members, await store.UserMapAsync(ct));
        return Results.Json(dto, GraphJson.Options, statusCode: created ? 201 : 200);
    }

    private static async Task<IResult> ListChatMessagesAsync(string chatId, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        await store.RequireChatAsync(chatId, ct);
        await RequireChatAccessAsync(http, store, chatId, ct);
        var query = MessageQuery(http, 20, 50);
        var (items, next) = await store.ListTeamsMessagesAsync(chatId, rootsOnly: false, replyToId: null, query, ct);
        return Collection(items.Select(GraphEmulatorMapper.TeamsMessage).ToList(), next is null ? null : PageLink(http.Request, next), null);
    }

    private static async Task<IResult> GetChatMessageAsync(string chatId, string messageId, HttpContext http, GraphEmulatorStore store,
        CancellationToken ct)
    {
        await RequireChatAccessAsync(http, store, chatId, ct);
        return Results.Json(GraphEmulatorMapper.TeamsMessage(await store.RequireTeamsMessageAsync(chatId, messageId, ct)), GraphJson.Options);
    }

    private static async Task<IResult> PostChatMessageAsync(string chatId, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var principal = RequireDelegated(http, "Posting a chat message");
        var draft = await ReadPostAsync(http, principal.UserId!, null, store, ct);
        var message = await store.PostChatMessageAsync(chatId, draft, ct);
        return Results.Json(GraphEmulatorMapper.TeamsMessage(message), GraphJson.Options, statusCode: 201);
    }

    private static async Task RequireChatAccessAsync(HttpContext http, GraphEmulatorStore store, string chatId, CancellationToken ct)
    {
        var principal = Principal(http);
        if (principal.IsDelegated && !await store.IsChatMemberAsync(chatId, principal.UserId!, ct))
            throw GraphEmulatorException.Forbidden("The signed-in user is not a member of this chat.");
    }

    // ═════════════════════════════ Channels ═════════════════════════════

    private static async Task<IResult> ListChannelMessagesAsync(string teamId, string channelId, HttpContext http, GraphEmulatorStore store,
        CancellationToken ct)
    {
        await store.RequireChannelAsync(teamId, channelId, ct);
        var query = MessageQuery(http, 20, 50);
        var (items, next) = await store.ListTeamsMessagesAsync(GraphEmulatorStore.ChannelContainer(teamId, channelId), rootsOnly: true, replyToId: null,
            query, ct);
        return Collection(items.Select(GraphEmulatorMapper.TeamsMessage).ToList(), next is null ? null : PageLink(http.Request, next), null);
    }

    private static async Task<IResult> PostChannelMessageAsync(string teamId, string channelId, HttpContext http, GraphEmulatorStore store,
        CancellationToken ct)
    {
        var principal = RequireDelegated(http, "Posting a channel message");
        var draft = await ReadPostAsync(http, principal.UserId!, null, store, ct);
        var message = await store.PostChannelMessageAsync(teamId, channelId, draft, ct);
        return Results.Json(GraphEmulatorMapper.TeamsMessage(message), GraphJson.Options, statusCode: 201);
    }

    private static async Task<IResult> PostReplyAsync(string teamId, string channelId, string messageId, HttpContext http, GraphEmulatorStore store,
        CancellationToken ct)
    {
        var principal = RequireDelegated(http, "Replying to a channel message");
        var draft = await ReadPostAsync(http, principal.UserId!, messageId, store, ct);
        var message = await store.PostChannelMessageAsync(teamId, channelId, draft, ct);
        return Results.Json(GraphEmulatorMapper.TeamsMessage(message), GraphJson.Options, statusCode: 201);
    }

    private static async Task<IResult> GetChannelMessageAsync(string teamId, string channelId, string messageId, GraphEmulatorStore store,
        CancellationToken ct) =>
        Results.Json(GraphEmulatorMapper.TeamsMessage(
            await store.RequireTeamsMessageAsync(GraphEmulatorStore.ChannelContainer(teamId, channelId), messageId, ct)), GraphJson.Options);

    private static async Task<IResult> ListRepliesAsync(string teamId, string channelId, string messageId, HttpContext http, GraphEmulatorStore store,
        CancellationToken ct)
    {
        var container = GraphEmulatorStore.ChannelContainer(teamId, channelId);
        await store.RequireTeamsMessageAsync(container, messageId, ct);
        var query = MessageQuery(http, 20, 50);
        var (items, next) = await store.ListTeamsMessagesAsync(container, rootsOnly: false, replyToId: messageId, query, ct);
        return Collection(items.Select(GraphEmulatorMapper.TeamsMessage).ToList(), next is null ? null : PageLink(http.Request, next), null);
    }

    private static async Task<IResult> ChannelDeltaAsync(string teamId, string channelId, HttpContext http, GraphEmulatorStore store,
        CancellationToken ct)
    {
        await store.RequireChannelAsync(teamId, channelId, ct);
        var since = FilterDate(http, "lastModifiedDateTime", "gt");
        var request = new DeltaRequest(NullIfEmpty(Query(http, "$deltatoken")), NullIfEmpty(Query(http, "$skiptoken")), since, PageSize(http, 50, 50));
        var result = await store.ChannelDeltaAsync(teamId, channelId, request, ct);
        return DeltaCollection(http, result.Items.Select(GraphEmulatorMapper.TeamsMessage).ToList(), result);
    }

    // ═════════════════════════════ Mail ═════════════════════════════

    private static async Task<IResult> MailDeltaAsync(string userId, string folder, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        var normalizedFolder = GraphEmulatorStore.NormalizeFolder(folder);
        var since = FilterDate(http, "receivedDateTime", "ge");
        var pageSize = PageSize(http, 10, 999);
        var request = new DeltaRequest(NullIfEmpty(Query(http, "$deltatoken")), NullIfEmpty(Query(http, "$skiptoken")), since, pageSize);
        var result = await store.MailDeltaAsync(user.Mail, normalizedFolder, request, ct);
        http.Response.Headers["Preference-Applied"] = $"odata.maxpagesize={pageSize}";
        return DeltaCollection(http, result.Items.Select(m => GraphEmulatorMapper.Mail(m)).ToList(), result);
    }

    private static async Task<IResult> ListFolderMessagesAsync(string userId, string folder, HttpContext http, GraphEmulatorStore store,
        CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        var normalizedFolder = GraphEmulatorStore.NormalizeFolder(folder);
        var filter = Query(http, "$filter");
        var sentSince = FilterDate(http, "sentDateTime", "ge") ?? FilterDate(http, "receivedDateTime", "ge");
        var items = await store.ListMailAsync(user.Mail, normalizedFolder, sentSince, 5000, ct);

        if (ExtendedPropertyFilterRegex().Match(filter) is { Success: true } match)
        {
            var propertyId = match.Groups["id"].Value.Replace("''", "'");
            var value = match.Groups["value"].Value.Replace("''", "'");
            items = items.Where(m => GraphEmulatorStore.FromJson<MailExtendedProperty>(m.ExtendedPropertiesJson)
                .Any(p => string.Equals(p.Id, propertyId, StringComparison.OrdinalIgnoreCase) && p.Value == value)).ToList();
        }
        var expandProperties = Query(http, "$expand").Contains("singleValueExtendedProperties", StringComparison.OrdinalIgnoreCase);
        return OffsetPage(http, items, 10, 999, m => GraphEmulatorMapper.Mail(m, expandProperties));
    }

    private static async Task<IResult> GetMessageAsync(string userId, string messageId, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        var item = await store.FindMailItemAsync(user.Mail, messageId, ct)
                   ?? throw new GraphEmulatorException(404, "ErrorItemNotFound", "The specified object was not found in the store.");
        var expandProperties = Query(http, "$expand").Contains("singleValueExtendedProperties", StringComparison.OrdinalIgnoreCase);
        return Results.Json(GraphEmulatorMapper.Mail(item, expandProperties), GraphJson.Options);
    }

    private static async Task<IResult> ListAttachmentsAsync(string userId, string messageId, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        _ = await store.FindMailItemAsync(user.Mail, messageId, ct)
            ?? throw new GraphEmulatorException(404, "ErrorItemNotFound", "The specified object was not found in the store.");
        return Collection(new List<GraphAttachment>(), null, null);
    }

    private static async Task<IResult> SendMailAsync(string userId, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        var principal = Principal(http);
        if (principal.IsDelegated && principal.UserId != user.Id)
            throw new GraphEmulatorException(403, "ErrorAccessDenied", "Access is denied. Check credentials and try again.");

        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct) as JsonObject
                   ?? throw GraphEmulatorException.BadRequest("A sendMail body is required.");
        var message = body["message"]?.Deserialize<GraphMessage>(GraphJson.Options)
                      ?? throw new GraphEmulatorException(400, "ErrorInvalidRequest", "The 'message' property is required.");
        var saveToSentItems = body["saveToSentItems"]?.GetValue<bool>() ?? true;

        static List<MailAddress> Addresses(List<GraphRecipient>? recipients) =>
            (recipients ?? []).Where(r => !string.IsNullOrWhiteSpace(r.EmailAddress?.Address))
            .Select(r => new MailAddress(r.EmailAddress!.Address!.Trim(), r.EmailAddress.Name)).ToList();
        var to = Addresses(message.ToRecipients);
        var cc = Addresses(message.CcRecipients);
        if (to.Count + cc.Count == 0)
            throw new GraphEmulatorException(400, "ErrorInvalidRecipients", "At least one recipient isn't valid. A message can't be sent without recipients.");

        var headers = new List<MailHeader>();
        foreach (var h in message.InternetMessageHeaders ?? [])
        {
            var threading = h.Name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase) || h.Name.Equals("References", StringComparison.OrdinalIgnoreCase);
            if (!threading && !h.Name.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                throw new GraphEmulatorException(400, "InvalidInternetMessageHeader",
                    $"The internet message header name '{h.Name}' should start with 'x-' or 'X-'.");
            headers.Add(new MailHeader(h.Name, h.Value));
        }
        var properties = (message.SingleValueExtendedProperties ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .Select(p => new MailExtendedProperty(p.Id, p.Value ?? ""))
            .ToList();
        var contentType = string.Equals(message.Body?.ContentType, "html", StringComparison.OrdinalIgnoreCase) ? "html" : "text";

        await store.DeliverMailAsync(new MailDraft(user.Mail, user.Mail, user.DisplayName, to, cc, message.Subject ?? "", contentType,
            message.Body?.Content ?? "", message.Importance ?? "normal", headers, properties, store.Now, SaveToSentItems: saveToSentItems), ct);
        return Results.StatusCode(202);
    }

    // ═════════════════════════════ Calendar ═════════════════════════════

    private static async Task<IResult> ListEventsAsync(string userId, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        var events = await store.ListEventsAsync(user.Mail, 5000, ct);
        return OffsetPage(http, events, 10, 999, GraphEmulatorMapper.Event);
    }

    private static async Task<IResult> CreateEventAsync(string userId, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        var request = await JsonSerializer.DeserializeAsync<GraphEvent>(http.Request.Body, GraphJson.Options, ct)
                      ?? throw GraphEmulatorException.BadRequest("An event body is required.");
        var (entity, _) = await store.CreateEventAsync(user.Mail, request, ct);
        return Results.Json(GraphEmulatorMapper.Event(entity), GraphJson.Options, statusCode: 201);
    }

    private static async Task<IResult> GetEventAsync(string userId, string eventId, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        return Results.Json(GraphEmulatorMapper.Event(await store.RequireEventAsync(user.Mail, eventId, ct)), GraphJson.Options);
    }

    private static async Task<IResult> UpdateEventAsync(string userId, string eventId, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        var patch = await JsonSerializer.DeserializeAsync<GraphEvent>(http.Request.Body, GraphJson.Options, ct)
                    ?? throw GraphEmulatorException.BadRequest("A patch body is required.");
        return Results.Json(GraphEmulatorMapper.Event(await store.UpdateEventAsync(user.Mail, eventId, patch, ct)), GraphJson.Options);
    }

    private static async Task<IResult> DeleteEventAsync(string userId, string eventId, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        await store.DeleteEventAsync(user.Mail, eventId, ct);
        return Results.NoContent();
    }

    // ═════════════════════════════ Activity feed ═════════════════════════════

    private static async Task<IResult> SendActivityAsync(string userId, HttpContext http, GraphEmulatorStore store, CancellationToken ct)
    {
        var user = await store.RequireUserAsync(userId, ct);
        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct) as JsonObject
                   ?? throw GraphEmulatorException.BadRequest("An activity notification body is required.");
        var activityType = body["activityType"]?.GetValue<string>();
        var topic = body["topic"] as JsonObject;
        if (string.IsNullOrWhiteSpace(activityType) || topic is null)
            throw GraphEmulatorException.BadRequest("'topic' and 'activityType' are required.");
        var parameters = (body["templateParameters"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(p => new MailHeader(p["name"]?.GetValue<string>() ?? "", p["value"]?.GetValue<string>() ?? ""))
            .ToList();
        await store.AddActivityAsync(new EmulatorActivityNotification
        {
            UserId = user.Id,
            ActivityType = activityType,
            TopicValue = topic["value"]?.GetValue<string>(),
            TopicWebUrl = topic["webUrl"]?.GetValue<string>(),
            PreviewText = body["previewText"]?["content"]?.GetValue<string>(),
            TemplateParametersJson = GraphEmulatorStore.ToJson(parameters),
            SenderAppId = Principal(http).ClientId,
        }, ct);
        return Results.NoContent();
    }

    // ═════════════════════════════ Helpers ═════════════════════════════

    /// <summary>Reads a chatMessage POST body into a draft (text bodies are converted to HTML, mentions resolved).</summary>
    private static async Task<TeamsPostDraft> ReadPostAsync(HttpContext http, string fromUserId, string? replyToId, GraphEmulatorStore store,
        CancellationToken ct)
    {
        var message = await JsonSerializer.DeserializeAsync<GraphChatMessage>(http.Request.Body, GraphJson.Options, ct)
                      ?? throw GraphEmulatorException.BadRequest("A message body is required.");
        var content = message.Body?.Content;
        if (string.IsNullOrWhiteSpace(content)) throw GraphEmulatorException.BadRequest("Message body content is required.");
        var html = string.Equals(message.Body?.ContentType, "html", StringComparison.OrdinalIgnoreCase) ? content : TeamsContent.TextToHtml(content);
        var mentions = new List<TeamsMentionTag>();
        foreach (var m in message.Mentions ?? [])
        {
            var userId = m.Mentioned?.User?.Id ?? throw GraphEmulatorException.BadRequest("Every mention needs mentioned.user.id.");
            var user = await store.RequireUserAsync(userId, ct);
            mentions.Add(new TeamsMentionTag(m.Id, user.Id, m.Mentioned.User.DisplayName ?? m.MentionText ?? user.DisplayName));
        }
        return new TeamsPostDraft(fromUserId, html, message.Subject, message.Importance ?? "normal", mentions, store.Now, replyToId);
    }

    private static IResult DeltaCollection<TItem, TEntity>(HttpContext http, List<TItem> items, DeltaResult<TEntity> result) =>
        Collection(items,
            result.SkipToken is null ? null : TokenLink(http.Request, "$skiptoken", result.SkipToken),
            result.DeltaToken is null ? null : TokenLink(http.Request, "$deltatoken", result.DeltaToken));

    private static IResult Collection<T>(List<T> items, string? nextLink, string? deltaLink) =>
        Results.Json(new GraphCollection<T> { Value = items, NextLink = nextLink, DeltaLink = deltaLink }, GraphJson.Options);

    /// <summary>Offset paging for small directory-style collections ($top + numeric $skiptoken).</summary>
    private static IResult OffsetPage<TEntity, TDto>(HttpContext http, IReadOnlyList<TEntity> all, int defaultTop, int maxTop, Func<TEntity, TDto> map)
    {
        var top = Top(http, defaultTop, maxTop);
        var skip = int.TryParse(Query(http, "$skiptoken"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var page = all.Skip(skip).Take(top).Select(map).ToList();
        var next = skip + top < all.Count ? PageLink(http.Request, (skip + top).ToString(CultureInfo.InvariantCulture)) : null;
        return Collection(page, next, null);
    }

    private static TeamsMessageQuery MessageQuery(HttpContext http, int defaultTop, int maxTop)
    {
        var orderBy = Query(http, "$orderby");
        var byLastModified = orderBy.Contains("lastModifiedDateTime", StringComparison.OrdinalIgnoreCase);
        return new TeamsMessageQuery(Top(http, defaultTop, maxTop), byLastModified, FilterDate(http, "lastModifiedDateTime", "gt"),
            FilterDate(http, "lastModifiedDateTime", "lt"), NullIfEmpty(Query(http, "$skiptoken")));
    }

    private static string Query(HttpContext http, string name) => http.Request.Query[name].ToString();

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int Top(HttpContext http, int @default, int max) =>
        int.TryParse(Query(http, "$top"), NumberStyles.None, CultureInfo.InvariantCulture, out var top) && top > 0 ? Math.Min(top, max) : @default;

    /// <summary>Page size from Prefer: odata.maxpagesize, else $top.</summary>
    private static int PageSize(HttpContext http, int @default, int max)
    {
        var prefer = http.Request.Headers["Prefer"].ToString();
        if (MaxPageSizeRegex().Match(prefer) is { Success: true } match
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size > 0)
            return Math.Min(size, max);
        return Top(http, @default, max);
    }

    /// <summary>Extracts "{property} {op} {timestamp}" from $filter.</summary>
    private static DateTime? FilterDate(HttpContext http, string property, string op)
    {
        var filter = Query(http, "$filter");
        if (filter.Length == 0) return null;
        var match = Regex.Match(filter, $@"\b{Regex.Escape(property)}\s+{op}\s+'?(?<value>[0-9][0-9T:\.\-\+Z]*)'?", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return GraphJson.TryParseDate(match.Groups["value"].Value, out var value)
            ? value
            : throw GraphEmulatorException.BadRequest($"Invalid timestamp in $filter: {match.Groups["value"].Value}");
    }

    /// <summary>The current URL with $skiptoken replaced (other query options kept).</summary>
    private static string PageLink(HttpRequest request, string skipToken)
    {
        var parts = request.Query
            .Where(q => !q.Key.Equals("$skiptoken", StringComparison.OrdinalIgnoreCase))
            .SelectMany(q => q.Value.Select(v => (q.Key, Value: v ?? "")))
            .Append(("$skiptoken", skipToken));
        return BaseUrl(request) + "?" + string.Join("&", parts.Select(p => EncodeKey(p.Item1) + "=" + Uri.EscapeDataString(p.Item2)));
    }

    /// <summary>Delta links carry only the opaque token, like Graph's.</summary>
    private static string TokenLink(HttpRequest request, string name, string token) => $"{BaseUrl(request)}?{name}={Uri.EscapeDataString(token)}";

    private static string BaseUrl(HttpRequest request)
    {
        var sb = new StringBuilder();
        sb.Append(request.Scheme).Append("://").Append(request.Host.Value).Append(request.PathBase.Value).Append(request.Path.Value);
        return sb.ToString();
    }

    private static string EncodeKey(string key) => key.StartsWith('$') ? "$" + Uri.EscapeDataString(key[1..]) : Uri.EscapeDataString(key);

    [GeneratedRegex(@"users(\('(?<id>(?:[^']|'')+)'\)|/(?<id>[^/?]+))", RegexOptions.IgnoreCase)]
    private static partial Regex UserBindRegex();

    [GeneratedRegex(@"ep/id\s+eq\s+'(?<id>(?:[^']|'')*)'\s+and\s+ep/value\s+eq\s+'(?<value>(?:[^']|'')*)'", RegexOptions.IgnoreCase)]
    private static partial Regex ExtendedPropertyFilterRegex();

    [GeneratedRegex(@"odata\.maxpagesize\s*=\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MaxPageSizeRegex();
}
