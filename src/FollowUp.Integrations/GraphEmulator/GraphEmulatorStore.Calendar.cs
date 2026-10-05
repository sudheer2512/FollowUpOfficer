using FollowUp.Integrations.Graph;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Integrations.GraphEmulator;

internal sealed partial class GraphEmulatorStore
{
    /// <summary>Creates an event; a known transactionId returns the event created by the earlier request instead.</summary>
    public Task<(EmulatorEvent Event, bool Created)> CreateEventAsync(string mailbox, GraphEvent request, CancellationToken ct) =>
        WriteAsync(async () =>
        {
            if (!string.IsNullOrWhiteSpace(request.TransactionId))
            {
                var existing = await _db.Events.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Mailbox == mailbox && e.TransactionId == request.TransactionId, ct);
                if (existing is not null) return (existing, false);
            }
            if (request.Start?.DateTime is null || request.End?.DateTime is null)
                throw new GraphEmulatorException(400, "ErrorInvalidRequest", "An event needs start and end.");
            var now = Now;
            var entity = new EmulatorEvent
            {
                Id = GraphEmulatorIds.NewEventId(),
                Mailbox = mailbox,
                TransactionId = string.IsNullOrWhiteSpace(request.TransactionId) ? null : request.TransactionId,
                CreatedDateTime = now,
            };
            Apply(entity, request, now);
            _db.Events.Add(entity);
            await _db.SaveChangesAsync(ct);
            return (entity, true);
        }, ct);

    /// <summary>Applies the properties present in a PATCH body.</summary>
    public Task<EmulatorEvent> UpdateEventAsync(string mailbox, string id, GraphEvent patch, CancellationToken ct) => WriteAsync(async () =>
    {
        var entity = await _db.Events.FirstOrDefaultAsync(e => e.Mailbox == mailbox && e.Id == id, ct)
                     ?? throw new GraphEmulatorException(404, "ErrorItemNotFound", "The specified object was not found in the store.");
        Apply(entity, patch, Now);
        await _db.SaveChangesAsync(ct);
        return entity;
    }, ct);

    public Task DeleteEventAsync(string mailbox, string id, CancellationToken ct) => WriteAsync(async () =>
    {
        var deleted = await _db.Events.Where(e => e.Mailbox == mailbox && e.Id == id).ExecuteDeleteAsync(ct);
        if (deleted == 0) throw new GraphEmulatorException(404, "ErrorItemNotFound", "The specified object was not found in the store.");
    }, ct);

    public async Task<EmulatorEvent> RequireEventAsync(string mailbox, string id, CancellationToken ct) =>
        await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Mailbox == mailbox && e.Id == id, ct)
        ?? throw new GraphEmulatorException(404, "ErrorItemNotFound", "The specified object was not found in the store.");

    public Task<List<EmulatorEvent>> ListEventsAsync(string mailbox, int max, CancellationToken ct) =>
        _db.Events.AsNoTracking().Where(e => e.Mailbox == mailbox).OrderBy(e => e.StartDateTime).Take(max).ToListAsync(ct);

    private static void Apply(EmulatorEvent entity, GraphEvent source, DateTime now)
    {
        if (source.Subject is not null) entity.Subject = source.Subject;
        if (source.Body?.Content is not null)
        {
            entity.BodyContent = source.Body.Content;
            entity.BodyContentType = string.Equals(source.Body.ContentType, "html", StringComparison.OrdinalIgnoreCase) ? "html" : "text";
        }
        if (source.Start?.DateTime is not null)
        {
            entity.StartDateTime = source.Start.DateTime;
            entity.StartTimeZone = source.Start.TimeZone ?? "UTC";
        }
        if (source.End?.DateTime is not null)
        {
            entity.EndDateTime = source.End.DateTime;
            entity.EndTimeZone = source.End.TimeZone ?? "UTC";
        }
        if (source.IsAllDay is { } allDay) entity.IsAllDay = allDay;
        if (source.Importance is not null) entity.Importance = NormalizeImportance(source.Importance);
        if (source.IsReminderOn is { } reminderOn) entity.IsReminderOn = reminderOn;
        if (source.ReminderMinutesBeforeStart is { } minutes) entity.ReminderMinutesBeforeStart = minutes;
        if (source.ShowAs is not null) entity.ShowAs = source.ShowAs;
        entity.LastModifiedDateTime = now;
    }
}
