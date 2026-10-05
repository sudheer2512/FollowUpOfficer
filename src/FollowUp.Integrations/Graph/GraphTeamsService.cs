using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations.Graph;

/// <summary>
/// Sends Teams messages. Chat and channel posts use the delegated service account (Graph only allows posting with
/// delegated permissions); a 1:1 chat is found or created with POST /chats (Graph returns the existing oneOnOne chat)
/// and cached per user. Activity-feed notifications are sent app-only.
/// </summary>
public sealed class GraphTeamsService : ITeamsService
{
    private readonly GraphClient _graph;
    private readonly IOptions<GraphOptions> _options;
    private readonly ILogger<GraphTeamsService> _logger;
    private readonly ConcurrentDictionary<string, string> _oneOnOneChats = new(StringComparer.OrdinalIgnoreCase);

    public GraphTeamsService(GraphClient graph, IOptions<GraphOptions> options, ILogger<GraphTeamsService> logger)
    {
        _graph = graph;
        _options = options;
        _logger = logger;
    }

    public async Task<DeliveryResult> SendAsync(OutboundTeamsMessage message, CancellationToken ct)
    {
        switch (message.Target)
        {
            case TeamsTargetKind.User:
            {
                var user = Require(message.UserId ?? message.UserAddress, "a user id or address");
                var chatId = await EnsureOneOnOneChatAsync(user, ct);
                try
                {
                    return await PostChatMessageAsync(chatId, message, ct);
                }
                catch (IntegrationException ex) when (ex.Kind == IntegrationErrorKind.NotFound)
                {
                    // The cached chat no longer exists: forget it, find/create it again and retry once.
                    _logger.LogInformation("Cached 1:1 chat {ChatId} for {User} was not found; recreating", chatId, user);
                    _oneOnOneChats.TryRemove(user, out _);
                    chatId = await EnsureOneOnOneChatAsync(user, ct);
                    return await PostChatMessageAsync(chatId, message, ct);
                }
            }
            case TeamsTargetKind.GroupChat:
                return await PostChatMessageAsync(Require(message.ChatId, "a chat id"), message, ct);
            case TeamsTargetKind.Channel:
            {
                var teamId = Require(message.TeamId, "a team id");
                var channelId = Require(message.ChannelId, "a channel id");
                var posted = await _graph.SendAsync<GraphChatMessage>(HttpMethod.Post,
                    $"teams/{GraphJson.Segment(teamId)}/channels/{GraphJson.Segment(channelId)}/messages", BuildMessage(message),
                    GraphAuth.ServiceAccount, ct);
                return new DeliveryResult(true, posted?.Id);
            }
            case TeamsTargetKind.ActivityFeed:
            {
                var user = Require(message.UserId ?? message.UserAddress, "a user id or address");
                await _graph.SendAsync(HttpMethod.Post, $"users/{GraphJson.Segment(user)}/teamwork/sendActivityNotification",
                    BuildActivity(message), GraphAuth.Application, ct);
                return new DeliveryResult(true, null);
            }
            default:
                throw new IntegrationException(IntegrationErrorKind.Validation, $"Unsupported Teams target {message.Target}.");
        }
    }

    /// <summary>Finds or creates the 1:1 chat between the service account and the user (id or UPN).</summary>
    internal async Task<string> EnsureOneOnOneChatAsync(string user, CancellationToken ct)
    {
        if (_oneOnOneChats.TryGetValue(user, out var cached)) return cached;
        var options = _options.Value;
        var body = new JsonObject
        {
            ["chatType"] = "oneOnOne",
            ["members"] = new JsonArray(Member(options.ServiceAccount), Member(user)),
        };
        var chat = await _graph.SendAsync<GraphChat>(HttpMethod.Post, "chats", body, GraphAuth.ServiceAccount, ct);
        if (chat?.Id is not { Length: > 0 } chatId)
            throw new IntegrationException(IntegrationErrorKind.Unknown, $"Creating the 1:1 chat with {user} returned no chat id.");
        _oneOnOneChats[user] = chatId;
        return chatId;
    }

    private async Task<DeliveryResult> PostChatMessageAsync(string chatId, OutboundTeamsMessage message, CancellationToken ct)
    {
        var posted = await _graph.SendAsync<GraphChatMessage>(HttpMethod.Post, $"chats/{GraphJson.Segment(chatId)}/messages",
            BuildMessage(message), GraphAuth.ServiceAccount, ct);
        return new DeliveryResult(true, posted?.Id);
    }

    private JsonObject Member(string user) => new()
    {
        ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
        ["roles"] = new JsonArray("owner"),
        ["user@odata.bind"] = $"{_graph.BaseUrl}/users('{user.Replace("'", "''", StringComparison.Ordinal)}')",
    };

    internal static JsonObject BuildMessage(OutboundTeamsMessage message)
    {
        var html = string.IsNullOrWhiteSpace(message.Html) ? TeamsContent.TextToHtml(message.Text) : message.Html;
        var (content, mentions) = TeamsContent.WithMentions(html,
            (message.Mentions ?? []).Select(m => (m.UserId, m.DisplayName)).ToList());
        var body = new JsonObject
        {
            ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = content },
            ["importance"] = message.Urgent ? "urgent" : "normal",
        };
        if (mentions.Count > 0) body["mentions"] = TeamsContent.ToJson(mentions);
        return body;
    }

    private JsonObject BuildActivity(OutboundTeamsMessage message)
    {
        var options = _options.Value;
        var text = message.Text.Trim();
        var preview = text.Length <= 150 ? text : text[..147] + "...";
        return new JsonObject
        {
            ["topic"] = new JsonObject
            {
                ["source"] = "text",
                ["value"] = options.ActivityFeedTopic,
                ["webUrl"] = options.ActivityFeedWebUrl,
            },
            // systemDefault needs no activity type declared in a Teams app manifest.
            ["activityType"] = "systemDefault",
            ["previewText"] = new JsonObject { ["content"] = preview },
            ["templateParameters"] = new JsonArray(new JsonObject { ["name"] = "systemDefaultText", ["value"] = text }),
        };
    }

    private static string Require(string? value, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new IntegrationException(IntegrationErrorKind.Validation, $"The Teams message needs {what}.")
            : value.Trim();
}
