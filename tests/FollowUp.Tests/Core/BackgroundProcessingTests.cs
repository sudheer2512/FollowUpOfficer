using FollowUp.Core.Contracts;
using FollowUp.Core.Jobs;
using FollowUp.Core.Services;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Tests.Core;

public class BackgroundProcessingTests
{
    private static Task<WorkTask> CreateAsync(TestApp app, CreateTaskRequest r) =>
        app.As("ceo", sp => sp.GetRequiredService<TaskService>().CreateAsync(r, default));

    private static Task<int> FollowUpsAsync(TestApp app) => app.As(null, sp => sp.GetRequiredService<FollowUpService>().ProcessDueFollowUpsAsync(default));

    [Fact]
    public async Task Each_follow_up_point_is_processed_exactly_once()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await CreateAsync(app, new CreateTaskRequest { Title = "Weekly MIS", PrimaryAssigneeId = app.People["dev"], DueDate = new DateOnly(2026, 10, 16), FollowUp = FollowUpSchedule.Daily(), RequiresAcknowledgement = false });
        var first = (await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id))).NextFollowUpAt!.Value;
        Assert.Equal(app.Local(new DateOnly(2026, 10, 6), 10), first);

        app.Clock.Pin(first.AddMinutes(5));
        Assert.Equal(1, await FollowUpsAsync(app));
        Assert.Equal(0, await FollowUpsAsync(app));   // re-run: nothing due

        // Another host (or a crash before the schedule advanced) tries the same point again: the unique key stops it.
        await app.Db(async db => { var x = await db.Tasks.FirstAsync(y => y.Id == t.Id); x.NextFollowUpAt = first; return await db.SaveChangesAsync(); });
        await FollowUpsAsync(app);
        Assert.Equal(1, await app.Db(db => db.TaskFollowUps.CountAsync(f => f.TaskId == t.Id)));
        Assert.Equal(1, await app.Db(db => db.Notifications.CountAsync(n => n.TaskId == t.Id && n.Category == NotificationCategory.Reminder && n.Channel == NotificationChannel.Email)));
    }

    [Fact]
    public async Task A_recent_update_suppresses_the_reminder()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await CreateAsync(app, new CreateTaskRequest { Title = "Collections", PrimaryAssigneeId = app.People["dev"], DueDate = new DateOnly(2026, 10, 16), FollowUp = FollowUpSchedule.Daily(), RequiresAcknowledgement = false });
        app.Clock.Pin(app.Local(new DateOnly(2026, 10, 6), 8));
        await app.As("dev", sp => sp.GetRequiredService<TaskService>().CommentAsync(t.Id, new CommentRequest("82% collected so far."), default));
        app.Clock.Pin(app.Local(new DateOnly(2026, 10, 6), 10, 5));
        await FollowUpsAsync(app);
        var log = await app.Db(db => db.TaskFollowUps.SingleAsync(f => f.TaskId == t.Id));
        Assert.Equal(FollowUpAction.Skip, log.Action);
        Assert.StartsWith("Update received 2h ago", log.Reason);
    }

    [Fact]
    public async Task Downtime_is_caught_up_with_a_single_reminder()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await CreateAsync(app, new CreateTaskRequest { Title = "Vendor onboarding", PrimaryAssigneeId = app.People["dev"], DueDate = new DateOnly(2026, 10, 30), FollowUp = FollowUpSchedule.Daily(), RequiresAcknowledgement = false });
        // Server stopped Tuesday morning, restarted Monday 12 Oct 11:00 — five daily points were missed.
        app.Clock.Pin(app.Local(new DateOnly(2026, 10, 12), 11));
        await FollowUpsAsync(app);
        var log = await app.Db(db => db.TaskFollowUps.SingleAsync(f => f.TaskId == t.Id));
        Assert.Equal(FollowUpAction.Remind, log.Action);
        Assert.Equal(5, log.CoalescedMissedPoints);
        Assert.Contains("caught up 5 missed point(s)", log.Reason);
        Assert.Equal(app.Local(new DateOnly(2026, 10, 13), 10), (await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id))).NextFollowUpAt);
    }

    [Fact]
    public async Task Points_reached_at_night_wait_for_the_working_morning()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await CreateAsync(app, new CreateTaskRequest { Title = "Night", PrimaryAssigneeId = app.People["dev"], FollowUp = FollowUpSchedule.Daily(), RequiresAcknowledgement = false });
        app.Clock.Pin(app.Local(new DateOnly(2026, 10, 7), 2));
        await FollowUpsAsync(app);
        Assert.Equal(0, await app.Db(db => db.TaskFollowUps.CountAsync()));
        Assert.Equal(app.Local(new DateOnly(2026, 10, 7), 10), (await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id))).NextFollowUpAt);
    }

    [Fact]
    public async Task Escalations_fire_once_and_only_the_highest_step_after_a_gap()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await CreateAsync(app, new CreateTaskRequest { Title = "Overdue", PrimaryAssigneeId = app.People["dev"], TeamId = app.TeamId, Criticality = Criticality.Important, DueDate = new DateOnly(2026, 10, 1), RequiresAcknowledgement = false });
        app.Clock.Pin(app.Local(new DateOnly(2026, 10, 8), 11));   // 7 days overdue
        await app.As(null, sp => sp.GetRequiredService<EscalationService>().ProcessAsync(default));
        await app.As(null, sp => sp.GetRequiredService<EscalationService>().ProcessAsync(default));
        var rows = await app.Db(db => db.TaskEscalations.Where(e => e.TaskId == t.Id).ToListAsync());
        Assert.Equal(4, rows.Count(r => r.Trigger is EscalationTrigger.OnDue or EscalationTrigger.Overdue));
        var live = rows.Single(r => !r.Reason.Contains("superseded"));
        Assert.Equal(4, live.Level);
        Assert.True(live.Targets.HasFlag(EscalationTarget.Ceo));
        Assert.Equal(1, await app.Db(db => db.Notifications.CountAsync(n => n.TaskId == t.Id && n.Category == NotificationCategory.CeoAlert && n.RecipientEmployeeId == app.People["ceo"] && n.Channel == NotificationChannel.Email)));
        Assert.True(await app.Db(db => db.AttentionItems.AnyAsync(a => a.TaskId == t.Id && a.Type == AttentionType.EscalatedToCeo)));
    }

    [Fact]
    public async Task Missed_recurring_occurrences_follow_the_catch_up_policy_and_are_never_lost()
    {
        await using var app = await TestApp.CreateAsync();
        var defId = await app.As("ceo", async sp => (await sp.GetRequiredService<RecurringTaskService>().CreateAsync(new RecurringDefinitionInput
        {
            TitleTemplate = "Daily critical support review — {Date}", PrimaryAssigneeId = app.People["dev"], Criticality = Criticality.Important, CatchUp = CatchUpPolicy.GenerateLatestOnly,
            Rule = new RecurrenceRule { Frequency = RecurrenceFrequency.WorkingDays, CreateTime = new TimeOnly(8, 0) }, Due = new DueRule { Kind = DueRuleKind.SameDay, DueTime = new TimeOnly(18, 0) },
            StartDate = new DateOnly(2026, 10, 5),
        }, default)).Id);
        await app.As(null, sp => sp.GetRequiredService<RecurringTaskService>().GenerateDueAsync(default));   // Monday 10:00 → today's occurrence

        // Server stopped Monday evening and restarted the next Monday at 09:40.
        app.Clock.Pin(app.Local(new DateOnly(2026, 10, 12), 9, 40));
        await app.As(null, sp => sp.GetRequiredService<RecurringTaskService>().GenerateDueAsync(default));
        await app.As(null, sp => sp.GetRequiredService<RecurringTaskService>().GenerateDueAsync(default));   // idempotent

        var instances = await app.Db(db => db.RecurringInstances.Where(i => i.DefinitionId == defId).OrderBy(i => i.NominalDate).ToListAsync());
        Assert.Equal([5, 6, 7, 8, 9, 12], instances.Select(i => i.NominalDate.Day));
        Assert.Equal(OccurrenceOutcome.Created, instances[0].Outcome);
        Assert.All(instances.Skip(1).Take(4), i => Assert.Equal(OccurrenceOutcome.SkippedCatchUp, i.Outcome));
        Assert.Contains("superseded by the 2026-10-12 occurrence", instances[1].Reason);
        Assert.Equal(OccurrenceOutcome.CreatedLate, instances[5].Outcome);
        Assert.Equal(2, await app.Db(db => db.Tasks.CountAsync(t => t.RecurringDefinitionId == defId)));
        Assert.True(await app.Db(db => db.AttentionItems.AnyAsync(a => a.Type == AttentionType.RecurringMissed)));
        var def = await app.Db(db => db.RecurringDefinitions.FirstAsync(d => d.Id == defId));
        Assert.Equal(new DateOnly(2026, 10, 12), def.LastProcessedOccurrence);
        Assert.Equal(app.Local(new DateOnly(2026, 10, 13), 8), def.NextOccurrenceAt);
    }

    [Fact]
    public async Task Skipping_a_future_occurrence_in_advance_prevents_it()
    {
        await using var app = await TestApp.CreateAsync();
        var defId = await app.As("ceo", async sp => (await sp.GetRequiredService<RecurringTaskService>().CreateAsync(new RecurringDefinitionInput
        {
            TitleTemplate = "Weekly review {Week}", PrimaryAssigneeId = app.People["dev"],
            Rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, DaysOfWeek = [DayOfWeek.Monday] }, Due = new DueRule { Kind = DueRuleKind.WeekdayOfSameWeek, Weekday = DayOfWeek.Friday },
            StartDate = new DateOnly(2026, 10, 6),
        }, default)).Id);
        await app.As("ceo", sp => sp.GetRequiredService<RecurringTaskService>().SkipAsync(defId, new SkipOccurrenceRequest(new DateOnly(2026, 10, 12), null, "Offsite"), default));
        app.Clock.Pin(app.Local(new DateOnly(2026, 10, 19), 8, 10));
        await app.As(null, sp => sp.GetRequiredService<RecurringTaskService>().GenerateDueAsync(default));
        var outcomes = await app.Db(db => db.RecurringInstances.Where(i => i.DefinitionId == defId).OrderBy(i => i.NominalDate).Select(i => i.Outcome).ToListAsync());
        Assert.Equal([OccurrenceOutcome.SkippedByUser, OccurrenceOutcome.Created], outcomes);
    }

    [Fact]
    public async Task Missed_commitments_are_flagged_and_notified()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await CreateAsync(app, new CreateTaskRequest { Title = "API study", PrimaryAssigneeId = app.People["dev"], Criticality = Criticality.Important, DueDate = new DateOnly(2026, 10, 20), RequiresAcknowledgement = false });
        await app.As("dev", sp => sp.GetRequiredService<TaskService>().CommentAsync(t.Id, new CommentRequest("We will complete this by Wednesday."), default));
        app.Clock.Pin(app.Local(new DateOnly(2026, 10, 8), 10));
        await app.As(null, sp => sp.GetRequiredService<CommitmentService>().EvaluateAsync(default));
        var c = await app.Db(db => db.Commitments.SingleAsync(x => x.TaskId == t.Id));
        Assert.Equal(CommitmentStatus.Missed, c.Status);
        Assert.True(await app.Db(db => db.Notifications.AnyAsync(n => n.Category == NotificationCategory.CommitmentMissed && n.RecipientEmployeeId == app.People["dev"])));
        Assert.True(await app.Db(db => db.AttentionItems.AnyAsync(a => a.Type == AttentionType.CommitmentMissed && a.TaskId == t.Id)));
        var missedToMe = await app.As("ceo", sp => sp.GetRequiredService<CommitmentService>().ListAsync(CommitmentStatus.Missed, toMe: true, null, null, null, default));
        Assert.Single(missedToMe);
    }

    [Fact]
    public void Job_schedules_use_local_wall_clock_time()
    {
        var cal = BusinessCalendar.Default;
        var job = new ScheduledJob { ScheduleKind = JobScheduleKind.DailyAt, TimeOfDay = new TimeOnly(8, 30) };
        Assert.Equal(cal.ToUtc(new DateOnly(2026, 10, 6), new TimeOnly(8, 30)), JobEngine.NextScheduled(job, TestApp.Monday10, cal));
        var weekly = new ScheduledJob { ScheduleKind = JobScheduleKind.WeeklyAt, TimeOfDay = new TimeOnly(9, 0), DayOfWeek = DayOfWeek.Monday };
        Assert.Equal(cal.ToUtc(new DateOnly(2026, 10, 12), new TimeOnly(9, 0)), JobEngine.NextScheduled(weekly, TestApp.Monday10, cal));
    }
}
