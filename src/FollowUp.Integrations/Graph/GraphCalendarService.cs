using System.Globalization;
using System.Text.Json.Nodes;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using Microsoft.Extensions.Logging;

namespace FollowUp.Integrations.Graph;

/// <summary>
/// Outlook calendar events for deadlines, reviews and approvals. Creates use the request key as Graph transactionId so a
/// retried POST cannot create a duplicate; updates PATCH the existing event (recreated if it was deleted in Outlook).
/// </summary>
public sealed class GraphCalendarService : ICalendarService
{
    private readonly GraphClient _graph;
    private readonly ILogger<GraphCalendarService> _logger;

    public GraphCalendarService(GraphClient graph, ILogger<GraphCalendarService> logger)
    {
        _graph = graph;
        _logger = logger;
    }

    public async Task<string> UpsertEventAsync(CalendarEventRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MailboxAddress))
            throw new IntegrationException(IntegrationErrorKind.Validation, "A calendar event needs a mailbox.");
        var events = $"users/{GraphJson.Segment(request.MailboxAddress.Trim())}/events";

        if (!string.IsNullOrWhiteSpace(request.ExistingEventId))
        {
            try
            {
                var updated = await _graph.SendAsync<GraphEvent>(HttpMethod.Patch, $"{events}/{GraphJson.Segment(request.ExistingEventId)}",
                    BuildEvent(request, includeTransactionId: false), GraphAuth.Application, ct);
                return updated?.Id ?? request.ExistingEventId;
            }
            catch (IntegrationException ex) when (ex.Kind == IntegrationErrorKind.NotFound)
            {
                _logger.LogInformation("Calendar event {EventId} no longer exists in {Mailbox}; creating it again", request.ExistingEventId, request.MailboxAddress);
            }
        }

        var created = await _graph.SendAsync<GraphEvent>(HttpMethod.Post, events, BuildEvent(request, includeTransactionId: true),
            GraphAuth.Application, ct);
        return created?.Id is { Length: > 0 } id
            ? id
            : throw new IntegrationException(IntegrationErrorKind.Unknown, "Creating the calendar event returned no event id.");
    }

    public async Task DeleteEventAsync(string mailboxAddress, string eventId, CancellationToken ct)
    {
        try
        {
            await _graph.SendAsync(HttpMethod.Delete, $"users/{GraphJson.Segment(mailboxAddress.Trim())}/events/{GraphJson.Segment(eventId)}",
                null, GraphAuth.Application, ct);
        }
        catch (IntegrationException ex) when (ex.Kind is IntegrationErrorKind.NotFound or IntegrationErrorKind.Gone)
        {
            // Already deleted (e.g. by the user in Outlook): deleting is idempotent.
            _logger.LogDebug("Calendar event {EventId} in {Mailbox} was already deleted", eventId, mailboxAddress);
        }
    }

    internal static JsonObject BuildEvent(CalendarEventRequest r, bool includeTransactionId)
    {
        string start, end;
        var allDay = r.Start is null;
        if (allDay)
        {
            start = Format(r.Date.ToDateTime(TimeOnly.MinValue));
            end = Format(r.Date.AddDays(1).ToDateTime(TimeOnly.MinValue));
        }
        else
        {
            var startAt = r.Date.ToDateTime(r.Start!.Value);
            var endAt = r.End is { } e && r.Date.ToDateTime(e) > startAt ? r.Date.ToDateTime(e) : startAt.AddMinutes(30);
            start = Format(startAt);
            end = Format(endAt);
        }
        var timeZone = string.IsNullOrWhiteSpace(r.TimeZoneId) ? "UTC" : r.TimeZoneId;
        var body = new JsonObject
        {
            ["subject"] = r.Subject,
            ["body"] = new JsonObject { ["contentType"] = "text", ["content"] = r.BodyText },
            ["start"] = new JsonObject { ["dateTime"] = start, ["timeZone"] = timeZone },
            ["end"] = new JsonObject { ["dateTime"] = end, ["timeZone"] = timeZone },
            ["isAllDay"] = allDay,
            ["importance"] = r.HighImportance ? "high" : "normal",
            ["showAs"] = allDay ? "free" : "busy",
            ["isReminderOn"] = r.ReminderMinutesBefore is not null,
        };
        if (r.ReminderMinutesBefore is { } minutes) body["reminderMinutesBeforeStart"] = minutes;
        if (includeTransactionId) body["transactionId"] = r.Key;
        return body;
    }

    private static string Format(DateTime local) => local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
