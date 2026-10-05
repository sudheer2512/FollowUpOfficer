using FollowUp.Core.Abstractions;
using FollowUp.Core.Contracts;
using FollowUp.Core.Services;
using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Tests.Core;

public class CommunicationTests
{
    private static InboundMessage Teams(string id, string from, string text, DateTime at) => new()
    {
        Channel = CommunicationChannel.TeamsChat, ExternalId = id, ConversationKey = "19:chat@unq", ConversationId = "19:chat@unq", BodyText = text,
        FromUserId = $"aad-{from}", FromName = from, SentAt = at, LastModifiedAt = at, SourceKey = "19:chat@unq",
    };

    private static async Task IngestAndProcessAsync(TestApp app, params InboundMessage[] messages)
    {
        await app.As(null, async sp =>
        {
            var svc = sp.GetRequiredService<CommunicationService>();
            foreach (var m in messages) await svc.IngestAsync(m, default);
            await sp.GetRequiredService<FollowUp.Core.Data.FollowUpDbContext>().SaveChangesAsync();
        });
        await app.As(null, sp => sp.GetRequiredService<CommunicationService>().ProcessPendingAsync(default));
    }

    [Fact]
    public async Task Explicit_task_number_links_and_records_the_commitment()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await app.As("ceo", sp => sp.GetRequiredService<TaskService>().CreateAsync(new CreateTaskRequest { Title = "Dealer CRM screens", PrimaryAssigneeId = app.People["dev"], DueDate = new DateOnly(2026, 10, 16), RequiresAcknowledgement = false }, default));
        await IngestAndProcessAsync(app, Teams("m1", "dev", $"{t.Number} will be completed by Thursday.", app.Clock.UtcNow), Teams("m1", "dev", "duplicate import", app.Clock.UtcNow));

        Assert.Equal(1, await app.Db(db => db.Messages.CountAsync()));
        var link = await app.Db(db => db.TaskMessageLinks.SingleAsync());
        Assert.Equal(LinkStatus.Linked, link.Status);
        Assert.Equal(MatchMethod.TaskNumber, link.Method);
        var c = await app.Db(db => db.Commitments.SingleAsync());
        Assert.Equal(CommunicationSource.TeamsChat, c.Source);
        Assert.Equal(new DateOnly(2026, 10, 8), c.CommitmentDate);
        Assert.NotNull((await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id))).LastAssigneeActivityAt);
    }

    [Fact]
    public async Task Waiting_message_is_matched_semantically_and_sets_the_blocker()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await app.As("ceo", sp => sp.GetRequiredService<TaskService>().CreateAsync(new CreateTaskRequest { Title = "UAE eInvoice Design", PrimaryAssigneeId = app.People["dev"], Criticality = Criticality.Critical, DueDate = new DateOnly(2026, 10, 16), RequiresAcknowledgement = false }, default));
        await IngestAndProcessAsync(app, Teams("m2", "dev", "On the UAE eInvoice design we are waiting for API credentials from the provider.", app.Clock.UtcNow));

        var link = await app.Db(db => db.TaskMessageLinks.SingleAsync());
        Assert.Equal(LinkStatus.Linked, link.Status);
        var task = await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id));
        Assert.Equal(WorkStatus.WaitingForExternalParty, task.Status);
        Assert.True(task.IsBlocked);
        Assert.Contains("api credentials", task.BlockedReason);
        Assert.Equal(SuggestionStatus.AutoApplied, (await app.Db(db => db.TaskSuggestions.SingleAsync())).Status);
    }

    [Fact]
    public async Task Reported_completion_never_closes_critical_work_by_itself()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await app.As("ceo", sp => sp.GetRequiredService<TaskService>().CreateAsync(new CreateTaskRequest { Title = "Payment gateway retry fix", PrimaryAssigneeId = app.People["dev"], Criticality = Criticality.Critical, RequiresAcknowledgement = false }, default));
        await IngestAndProcessAsync(app, Teams("m3", "dev", $"{t.Number}: fixed and deployed to UAT. Task completed.", app.Clock.UtcNow));

        Assert.Equal(WorkStatus.Assigned, (await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id))).Status);
        var s = await app.Db(db => db.TaskSuggestions.SingleAsync());
        Assert.Equal(SuggestionKind.MarkCompletedByAssignee, s.Kind);
        Assert.Equal(SuggestionStatus.Pending, s.Status);
        Assert.True(await app.Db(db => db.AttentionItems.AnyAsync(a => a.Type == AttentionType.AiSuggestion)));

        // A human decision closes it: the assignee confirming submits for approval; the delegator confirming completes it.
        await app.As("dev", sp => sp.GetRequiredService<TaskService>().DecideSuggestionAsync(s.Id, true, default));
        Assert.Equal(WorkStatus.CompletionAwaitingApproval, (await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id))).Status);
        await app.As("ceo", sp => sp.GetRequiredService<TaskService>().DecideApprovalAsync(t.Id, new ApprovalRequest(ApprovalAction.Confirm, null), default));
        Assert.Equal(WorkStatus.Completed, (await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id))).Status);
    }

    [Fact]
    public async Task Weak_matches_go_to_the_review_queue_and_own_messages_are_ignored()
    {
        await using var app = await TestApp.CreateAsync();
        await app.As("ceo", sp => sp.GetRequiredService<TaskService>().CreateAsync(new CreateTaskRequest { Title = "Quarterly vendor review pack", PrimaryAssigneeId = app.People["dev"], RequiresAcknowledgement = false }, default));
        var weak = Teams("m4", "dev", "Shared the vendor review notes with procurement.", app.Clock.UtcNow);
        var own = Teams("m5", "followup", "Task requires an update. — Digital Follow-up Officer", app.Clock.UtcNow) with { FromAddress = "followup@test.example" };
        await IngestAndProcessAsync(app, weak, own);

        var messages = await app.Db(db => db.Messages.OrderBy(m => m.ExternalId).ToListAsync());
        Assert.Equal(MessageProcessingStatus.NeedsReview, messages[0].ProcessingStatus);
        Assert.Equal(MessageProcessingStatus.Ignored, messages[1].ProcessingStatus);
        Assert.True(messages[1].IsSystemGenerated);
        var queue = await app.As("ceo", sp => sp.GetRequiredService<CommunicationService>().ReviewQueueAsync(default));
        var link = Assert.Single(Assert.Single(queue).Links);
        Assert.Equal(LinkStatus.Suggested, link.Status);

        await app.As("ceo", sp => sp.GetRequiredService<CommunicationService>().DecideLinkAsync(link.LinkId, true, default));
        Assert.Equal(LinkStatus.Linked, (await app.Db(db => db.TaskMessageLinks.SingleAsync())).Status);
        Assert.Equal(MessageProcessingStatus.Processed, (await app.Db(db => db.Messages.FirstAsync(m => m.ExternalId == "m4"))).ProcessingStatus);
    }

    [Fact]
    public async Task External_urgent_mail_to_the_ceo_raises_attention()
    {
        await using var app = await TestApp.CreateAsync();
        var mail = new InboundMessage
        {
            Channel = CommunicationChannel.Email, ExternalId = "<x1@customer>", ConversationKey = "mail", Subject = "URGENT: invoices failing", BodyText = "This is urgent, our dispatch is stopped.",
            FromAddress = "ops@customer.example", To = [new MessageParty("ceo@test.example", "Ceo")], SentAt = app.Clock.UtcNow, LastModifiedAt = app.Clock.UtcNow, SourceKey = "ceo@test.example|inbox",
        };
        await IngestAndProcessAsync(app, mail);
        var item = await app.Db(db => db.AttentionItems.SingleAsync());
        Assert.Equal(AttentionType.CustomerEscalation, item.Type);
    }
}
