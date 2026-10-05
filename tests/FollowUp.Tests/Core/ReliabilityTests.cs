using FollowUp.Core.Abstractions;
using FollowUp.Core.Contracts;
using FollowUp.Core.Services;
using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Tests.Core;

public class ReliabilityTests
{
    private static async Task<Guid> QueueEmailAsync(TestApp app, Action<Notification>? tweak = null)
    {
        var t = await app.As("ceo", sp => sp.GetRequiredService<TaskService>().CreateAsync(new CreateTaskRequest { Title = "Queued", PrimaryAssigneeId = app.People["dev"], RequiresAcknowledgement = false }, default));
        return await app.Db(async db =>
        {
            var n = await db.Notifications.FirstAsync(x => x.TaskId == t.Id && x.Channel == NotificationChannel.Email);
            tweak?.Invoke(n);
            await db.SaveChangesAsync();
            return n.Id;
        });
    }

    private static Task<int> DispatchAsync(TestApp app) => app.As(null, sp => sp.GetRequiredService<NotificationDispatcher>().DispatchAsync(default));

    [Fact]
    public async Task Throttled_sends_are_retried_after_the_retry_after_delay()
    {
        await using var app = await TestApp.CreateAsync();
        var id = await QueueEmailAsync(app);
        app.Email.Failures.Enqueue(new IntegrationException(IntegrationErrorKind.Throttled, "429", 429, TimeSpan.FromSeconds(30)));
        await DispatchAsync(app);
        var n = await app.Db(db => db.Notifications.FirstAsync(x => x.Id == id));
        Assert.Equal(NotificationStatus.Failed, n.Status);
        Assert.Equal(app.Clock.UtcNow.AddSeconds(30), n.NextAttemptAt);
        Assert.True(await app.Db(db => db.IntegrationErrors.AnyAsync(e => e.Kind == IntegrationErrorKind.Throttled)));

        app.Clock.Advance(TimeSpan.FromSeconds(31));
        await DispatchAsync(app);
        Assert.Equal(NotificationStatus.Sent, (await app.Db(db => db.Notifications.FirstAsync(x => x.Id == id))).Status);
        Assert.Single(app.Email.Sent, e => e.IdempotencyKey == n.IdempotencyKey);
    }

    [Fact]
    public async Task Permanent_failures_are_dead_lettered_and_can_be_retried()
    {
        await using var app = await TestApp.CreateAsync();
        var id = await QueueEmailAsync(app);
        app.Email.Failures.Enqueue(new IntegrationException(IntegrationErrorKind.Validation, "bad recipient", 400));
        app.Email.Failures.Enqueue(new IntegrationException(IntegrationErrorKind.Validation, "bad recipient", 400));
        await DispatchAsync(app);
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await DispatchAsync(app);
        Assert.Equal(NotificationStatus.DeadLetter, (await app.Db(db => db.Notifications.FirstAsync(x => x.Id == id))).Status);

        await app.As(null, sp => sp.GetRequiredService<NotificationDispatcher>().RetryAsync([id], default));
        await DispatchAsync(app);
        Assert.Equal(NotificationStatus.Sent, (await app.Db(db => db.Notifications.FirstAsync(x => x.Id == id))).Status);
    }

    [Fact]
    public async Task Stale_reminders_expire_instead_of_flooding_after_downtime()
    {
        await using var app = await TestApp.CreateAsync();
        var id = await QueueEmailAsync(app, n => n.ExpiresAt = app.Clock.UtcNow.AddHours(1));
        app.Clock.Advance(TimeSpan.FromDays(2));
        await DispatchAsync(app);
        Assert.Equal(NotificationStatus.Expired, (await app.Db(db => db.Notifications.FirstAsync(x => x.Id == id))).Status);
        Assert.Empty(app.Email.Sent);
    }

    [Fact]
    public async Task A_send_interrupted_by_a_crash_is_reconciled_not_duplicated()
    {
        await using var app = await TestApp.CreateAsync();
        string key = "";
        var id = await QueueEmailAsync(app, n =>
        {
            n.Status = NotificationStatus.Sending;             // the process died after the provider accepted the mail
            n.LastAttemptAt = app.Clock.UtcNow.AddMinutes(-10);
            key = n.IdempotencyKey;
        });
        app.Email.AlreadySent.Add(key);
        await DispatchAsync(app);
        var n = await app.Db(db => db.Notifications.FirstAsync(x => x.Id == id));
        Assert.Equal(NotificationStatus.Sent, n.Status);
        Assert.Contains("found in Sent Items", n.Error);
        Assert.DoesNotContain(app.Email.Sent, e => e.IdempotencyKey == key);
    }

    private static InboundMessage Mail(string id, string subject, string body, DateTime sentAt, string from = "dev@test.example", Dictionary<string, string>? headers = null) => new()
    {
        Channel = CommunicationChannel.Email, ExternalId = $"<{id}@test>", ConversationKey = "mail", ConversationId = $"conv-{id}", Subject = subject, BodyText = body,
        FromAddress = from, FromName = from, To = [new MessageParty("ceo@test.example", "Ceo")], SentAt = sentAt, LastModifiedAt = sentAt,
        Headers = headers ?? [],
    };

    [Fact]
    public async Task Sync_resumes_from_the_last_committed_page_after_a_failure()
    {
        await using var app = await TestApp.CreateAsync();
        await app.As(null, sp => sp.GetRequiredService<SyncService>().DiscoverAsync(default));
        var t0 = app.Clock.UtcNow.AddHours(-2);
        app.MailSync.Steps.Enqueue(_ => new SyncPage([Mail("a", "Hello", "one", t0), Mail("b", "Hello", "two", t0.AddMinutes(1))], "next-1", null, t0.AddMinutes(1), false));
        app.MailSync.Steps.Enqueue(_ => throw new IntegrationException(IntegrationErrorKind.Transient, "Graph unavailable", 503));
        await app.As(null, sp => sp.GetRequiredService<SyncService>().SyncMailAsync(default));

        var cp = await app.Db(db => db.SyncCheckpoints.SingleAsync());
        Assert.Equal("next-1", cp.ContinuationToken);
        Assert.Equal(SyncState.Failed, cp.Status);
        Assert.Equal(2, await app.Db(db => db.Messages.CountAsync()));

        // Restart: the next run continues from next-1; an overlapping duplicate is absorbed.
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        app.MailSync.Steps.Enqueue(c => c.ContinuationToken == "next-1"
            ? new SyncPage([Mail("b", "Hello", "two", t0.AddMinutes(1)), Mail("c", "Hello", "three", t0.AddMinutes(2))], null, "delta-1", t0.AddMinutes(2), true)
            : throw new InvalidOperationException("did not resume from the checkpoint"));
        await app.As(null, sp => sp.GetRequiredService<SyncService>().SyncMailAsync(default));
        cp = await app.Db(db => db.SyncCheckpoints.SingleAsync());
        Assert.Equal(3, await app.Db(db => db.Messages.CountAsync()));
        Assert.Equal("delta-1", cp.DeltaToken);
        Assert.Null(cp.ContinuationToken);
        Assert.Equal(SyncState.Succeeded, cp.Status);
        Assert.Equal(t0.AddMinutes(2), cp.LastProcessedAt);
    }

    [Fact]
    public async Task An_expired_delta_token_triggers_a_resync_from_the_watermark()
    {
        await using var app = await TestApp.CreateAsync();
        await app.As(null, sp => sp.GetRequiredService<SyncService>().DiscoverAsync(default));
        await app.Db(async db => { var c = await db.SyncCheckpoints.SingleAsync(); c.DeltaToken = "old"; c.LastProcessedAt = app.Clock.UtcNow.AddHours(-1); return await db.SaveChangesAsync(); });
        app.MailSync.Steps.Enqueue(_ => throw new IntegrationException(IntegrationErrorKind.Gone, "syncStateNotFound", 410));
        app.MailSync.Steps.Enqueue(c => c.DeltaToken is null && c.Watermark is not null
            ? new SyncPage([], null, "fresh", c.Watermark, true)
            : throw new InvalidOperationException("expected a resync"));
        await app.As(null, sp => sp.GetRequiredService<SyncService>().SyncMailAsync(default));
        Assert.Equal("fresh", (await app.Db(db => db.SyncCheckpoints.SingleAsync())).DeltaToken);
        Assert.True(await app.Db(db => db.IntegrationErrors.AnyAsync(e => e.Kind == IntegrationErrorKind.Gone)));
    }
}
