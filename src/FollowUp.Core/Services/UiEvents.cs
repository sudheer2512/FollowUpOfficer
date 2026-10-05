using FollowUp.Core.Data;
using FollowUp.Domain;

namespace FollowUp.Core.Services;

/// <summary>
/// Real-time UI events. Appended to the same DbContext as the business change (so they commit atomically);
/// the API relays new rows to SignalR clients. Works when the change happens in the separate Worker process.
/// </summary>
public static class UiEvents
{
    public const string TaskChanged = "task.changed";
    public const string TaskCreated = "task.created";
    public const string AttentionChanged = "attention.changed";
    public const string CommunicationImported = "communication.imported";
    public const string NotificationQueued = "notification.queued";
    public const string SyncStatus = "sync.status";
    public const string JobStatus = "job.status";
    public const string InboxChanged = "inbox.changed";

    public static void Publish(FollowUpDbContext db, IClock clock, string topic, Guid? taskId = null, Guid? audienceId = null, string? payload = null) =>
        db.UiEvents.Add(new UiEvent { At = clock.UtcNow, Topic = topic, TaskId = taskId, AudienceId = audienceId, Payload = payload });
}
