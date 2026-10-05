using FollowUp.Core.Common;
using FollowUp.Core.Data;
using FollowUp.Core.Services;
using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Jobs;

// Background jobs (spec §58). Each job is a thin, idempotent wrapper around a service that processes
// "everything due up to now" from persisted state — so a run after downtime naturally catches up, and the
// per-item rules (catch-up coalescing, expiry, unique keys) prevent duplicates and floods.

public sealed class RecurrenceJob(IRecurringTaskService service) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("recurrence.generate", "Recurring task generation",
        "Creates due recurring occurrences; after downtime records missed occurrences and applies each definition's catch-up policy.",
        JobScheduleKind.Interval, 60, TimeoutSeconds: 600);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await service.GenerateDueAsync(ct);
        return new(n, n > 0 ? $"{n} occurrence(s) created" : null);
    }
}

public sealed class FollowUpJob(IReminderService service) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("followup.process", "Follow-up processing",
        "Evaluates due follow-up points and sends reminders only when needed (no reminder after a recent update); coalesces points missed during downtime.",
        JobScheduleKind.Interval, 60, TimeoutSeconds: 600);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await service.ProcessDueFollowUpsAsync(ct);
        return new(n, n > 0 ? $"{n} follow-up point(s) evaluated" : null);
    }
}

public sealed class DeadlineJob(IReminderService service) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("deadline.process", "Deadline processing",
        "Due-tomorrow, due-today and now-overdue notices (once per deadline) during working hours.",
        JobScheduleKind.Interval, 300);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await service.ProcessDeadlinesAsync(ct);
        return new(n, n > 0 ? $"{n} deadline notice(s) queued" : null);
    }
}

public sealed class EscalationJob(IEscalationService service) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("escalation.process", "Escalation processing",
        "Applies escalation ladders (overdue, no response, unacknowledged, missed commitments, blocked); each step fires once per cycle.",
        JobScheduleKind.Interval, 300, TimeoutSeconds: 600);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await service.ProcessAsync(ct);
        return new(n, n > 0 ? $"{n} escalation(s) fired" : null);
    }
}

public sealed class CommitmentJob(CommitmentService service) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("commitment.evaluate", "Commitment tracking",
        "Marks commitments met or missed once their date passes and notifies the person and the delegator.",
        JobScheduleKind.Interval, 300);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await service.EvaluateAsync(ct);
        return new(n, n > 0 ? $"{n} commitment(s) evaluated" : null);
    }
}

public sealed class SourceDiscoveryJob(ISynchronizationCheckpointService service) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("sources.discover", "Source discovery",
        "Finds authorised mailboxes, chats and channels and creates synchronisation checkpoints for new ones.",
        JobScheduleKind.Interval, 1800);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await service.DiscoverAsync(ct);
        return new(n, n > 0 ? $"{n} new source(s)" : null);
    }
}

public sealed class MailSyncJob(ISynchronizationCheckpointService service) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("mail.sync", "Outlook mail synchronisation",
        "Delta-synchronises monitored mailboxes (inbox and sent items) from the last committed checkpoint.",
        JobScheduleKind.Interval, 60, TimeoutSeconds: 600);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await service.SyncMailAsync(ct);
        return new(n, n > 0 ? $"{n} email(s) imported" : null);
    }
}

public sealed class TeamsSyncJob(ISynchronizationCheckpointService service) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("teams.sync", "Teams synchronisation",
        "Synchronises monitored Teams chats (watermark) and channels (delta + replies) from the last committed checkpoint.",
        JobScheduleKind.Interval, 60, TimeoutSeconds: 600);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await service.SyncTeamsAsync(ct);
        return new(n, n > 0 ? $"{n} Teams message(s) imported" : null);
    }
}

public sealed class CommunicationJob(CommunicationService service) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("communication.process", "AI classification & correlation",
        "Correlates imported emails and Teams messages with tasks, classifies them, extracts commitments and applies the effects.",
        JobScheduleKind.Interval, 20, TimeoutSeconds: 600);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await service.ProcessPendingAsync(ct);
        return new(n, n > 0 ? $"{n} message(s) processed" : null);
    }
}

public sealed class NotificationJob(NotificationDispatcher dispatcher) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("notification.dispatch", "Notification delivery",
        "Delivers queued email and Teams notifications with retry, expiry and dead-lettering; reconciles sends interrupted by a crash.",
        JobScheduleKind.Interval, 15, TimeoutSeconds: 300);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await dispatcher.DispatchAsync(ct);
        return new(n, n > 0 ? $"{n} notification(s) delivered" : null);
    }
}

public sealed class AttentionJob(MonitoringService monitoring) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("attention.refresh", "Health & attention refresh",
        "Recomputes task health and attention scores and keeps the CEO attention queue current.",
        JobScheduleKind.Interval, 300, TimeoutSeconds: 900);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await monitoring.SweepAsync(ct);
        return new(n, $"{n} open task(s) refreshed");
    }
}

public sealed class MaintenanceJob(FollowUpDbContext db, IClock clock, IOptions<FollowUpOptions> options) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("maintenance.cleanup", "Maintenance",
        "Purges old real-time events and job history beyond retention.",
        JobScheduleKind.DailyAt, TimeOfDay: new TimeOnly(3, 0));

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var events = await db.UiEvents.Where(e => e.At < now.AddHours(-options.Value.UiEventRetentionHours)).ExecuteDeleteAsync(ct);
        var executions = await db.JobExecutions.Where(e => e.StartedAt < now.AddDays(-options.Value.JobHistoryRetentionDays)).ExecuteDeleteAsync(ct);
        return new(events + executions, $"Purged {events} UI event(s) and {executions} job execution(s)");
    }
}
