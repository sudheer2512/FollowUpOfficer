using System.Text.Json.Nodes;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations.Graph;

/// <summary>
/// Sends reminder email through Graph sendMail from the Follow-up Officer mailbox. Every message is stamped with the
/// idempotency key twice: an "X-FollowUp-Key" internet header (visible to sync and to replies' tooling) and a named
/// MAPI string property, which Sent Items can be filtered on to reconcile a crash between send and commit.
/// </summary>
public sealed class GraphEmailService : IEmailService
{
    public const string FollowUpKeyHeader = "X-FollowUp-Key";

    /// <summary>Named string property in the PS_PUBLIC_STRINGS property set.</summary>
    public const string FollowUpKeyPropertyId = "String {00020329-0000-0000-C000-000000000046} Name FollowUpKey";

    private readonly GraphClient _graph;
    private readonly IOptions<GraphOptions> _options;
    private readonly Microsoft365Clock _clock;
    private readonly ILogger<GraphEmailService> _logger;

    public GraphEmailService(GraphClient graph, IOptions<GraphOptions> options, Microsoft365Clock clock, ILogger<GraphEmailService> logger)
    {
        _graph = graph;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DeliveryResult> SendAsync(OutboundEmail email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email.IdempotencyKey))
            throw new IntegrationException(IntegrationErrorKind.Validation, "An email needs an idempotency key.");
        if (email.To.Count == 0 && email.Cc.Count == 0)
            throw new IntegrationException(IntegrationErrorKind.Validation, "An email needs at least one recipient.");

        var mailbox = string.IsNullOrWhiteSpace(email.FromMailbox) ? _options.Value.SenderMailbox : email.FromMailbox.Trim();
        // Lower bound for the post-send lookup: the earlier of business and real time (the demo clock may be shifted).
        var clockNow = _clock.UtcNow;
        var since = (clockNow < DateTime.UtcNow ? clockNow : DateTime.UtcNow).AddMinutes(-15);
        var isHtml = !string.IsNullOrWhiteSpace(email.BodyHtml);
        var body = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["subject"] = email.Subject,
                ["importance"] = email.HighImportance ? "high" : "normal",
                ["body"] = new JsonObject
                {
                    ["contentType"] = isHtml ? "HTML" : "Text",
                    ["content"] = isHtml ? email.BodyHtml : email.BodyText,
                },
                ["toRecipients"] = Recipients(email.To),
                ["ccRecipients"] = Recipients(email.Cc),
                ["internetMessageHeaders"] = new JsonArray(new JsonObject { ["name"] = FollowUpKeyHeader, ["value"] = email.IdempotencyKey }),
                ["singleValueExtendedProperties"] = new JsonArray(new JsonObject { ["id"] = FollowUpKeyPropertyId, ["value"] = email.IdempotencyKey }),
            },
            ["saveToSentItems"] = true,
        };

        await _graph.SendAsync(HttpMethod.Post, $"users/{GraphJson.Segment(mailbox)}/sendMail", body, GraphAuth.Application, ct);

        string? internetMessageId = null;
        if (_options.Value.ResolveSentMessageId)
        {
            try
            {
                internetMessageId = (await FindSentAsync(mailbox, email.IdempotencyKey, since, ct))?.InternetMessageId;
            }
            catch (IntegrationException ex)
            {
                // The mail was accepted; only the id lookup failed. Never fail (and so re-send) a delivered reminder.
                _logger.LogWarning(ex, "Mail {Key} was sent but its Internet-Message-Id could not be resolved ({Kind})", email.IdempotencyKey, ex.Kind);
            }
        }
        return new DeliveryResult(true, internetMessageId);
    }

    public async Task<bool?> WasSentAsync(string idempotencyKey, DateTime sinceUtc, CancellationToken ct) =>
        await FindSentAsync(_options.Value.SenderMailbox, idempotencyKey, sinceUtc, ct) is not null;

    /// <summary>Finds a sent message by the FollowUpKey extended property.</summary>
    internal async Task<GraphMessage?> FindSentAsync(string mailbox, string key, DateTime sinceUtc, CancellationToken ct)
    {
        var filter = $"singleValueExtendedProperties/Any(ep: ep/id eq {GraphJson.ODataString(FollowUpKeyPropertyId)} and ep/value eq {GraphJson.ODataString(key)})"
                     + $" and sentDateTime ge {GraphJson.FormatDate(sinceUtc)}";
        var url = $"users/{GraphJson.Segment(mailbox)}/mailFolders/sentitems/messages?$filter={Uri.EscapeDataString(filter)}"
                  + "&$select=id,internetMessageId,sentDateTime,subject&$top=1";
        var page = await _graph.GetPageAsync<GraphMessage>(url, GraphAuth.Application, ct);
        return page.Value.FirstOrDefault();
    }

    private static JsonArray Recipients(IReadOnlyList<EmailAddress> addresses)
    {
        var array = new JsonArray();
        foreach (var a in addresses.Where(a => !string.IsNullOrWhiteSpace(a.Address)))
        {
            var email = new JsonObject { ["address"] = a.Address.Trim() };
            if (!string.IsNullOrWhiteSpace(a.Name)) email["name"] = a.Name;
            array.Add(new JsonObject { ["emailAddress"] = email });
        }
        return array;
    }
}
