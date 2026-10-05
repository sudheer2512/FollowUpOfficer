using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Core.Services;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Tests.Core;

public class TaskLifecycleTests
{
    private static Task<WorkTask> CreateAsync(TestApp app, string who, CreateTaskRequest r) =>
        app.As(who, sp => sp.GetRequiredService<TaskService>().CreateAsync(r, default));

    [Fact]
    public async Task Delegation_assigns_numbers_requests_acknowledgement_and_keeps_the_chain()
    {
        await using var app = await TestApp.CreateAsync();
        var t1 = await CreateAsync(app, "ceo", new CreateTaskRequest { Title = "UAE eInvoice Design", PrimaryAssigneeId = app.People["cto"], Criticality = Criticality.Important, DueDate = new DateOnly(2026, 10, 9) });
        var t2 = await CreateAsync(app, "ceo", new CreateTaskRequest { Title = "Second", PrimaryAssigneeId = app.People["dev"] });
        Assert.Equal("TASK-2026-00001", t1.Number);
        Assert.Equal("TASK-2026-00002", t2.Number);
        Assert.Equal(WorkStatus.AcknowledgementPending, t1.Status);

        // CEO → CTO → Development lead: original delegator is kept, current delegator changes.
        await app.As("cto", sp => sp.GetRequiredService<TaskService>().ReassignAsync(t1.Id, new ReassignRequest(app.People["lead"], "Please own this"), default));
        var task = await app.Db(db => db.Tasks.AsNoTracking().FirstAsync(t => t.Id == t1.Id));
        Assert.Equal(app.People["lead"], task.PrimaryAssigneeId);
        Assert.Equal(app.People["ceo"], task.OriginalDelegatorId);
        Assert.Equal(app.People["cto"], task.DelegatorId);
        var chain = await app.Db(db => db.TaskDelegations.Where(d => d.TaskId == t1.Id).OrderBy(d => d.Sequence).Select(d => d.ToEmployeeId).ToListAsync());
        Assert.Equal([app.People["cto"], app.People["lead"]], chain);
        // Assignment notices went to both delegates (queued, not yet delivered).
        var queued = await app.Db(db => db.Notifications.Where(n => n.TaskId == t1.Id && n.Channel == NotificationChannel.Email).Select(n => n.RecipientEmployeeId).ToListAsync());
        Assert.Contains(app.People["cto"], queued);
        Assert.Contains(app.People["lead"], queued);
    }

    [Fact]
    public async Task Proposing_a_deadline_creates_an_extension_for_the_ceo_and_keeps_the_original_date()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await CreateAsync(app, "ceo", new CreateTaskRequest { Title = "Design", PrimaryAssigneeId = app.People["dev"], Criticality = Criticality.Important, DueDate = new DateOnly(2026, 10, 9) });
        await app.As("dev", sp => sp.GetRequiredService<TaskService>().AcknowledgeAsync(t.Id, new AcknowledgeRequest(AcknowledgementResponse.ProposeDifferentDeadline, "Need provider credentials", new DateOnly(2026, 10, 16)), default));

        var ext = await app.Db(db => db.ExtensionRequests.SingleAsync(x => x.TaskId == t.Id));
        Assert.Equal(app.People["ceo"], ext.ApproverId);
        Assert.True(await app.Db(db => db.AttentionItems.AnyAsync(a => a.Type == AttentionType.ExtensionRequest && a.TaskId == t.Id)));

        await app.As("ceo", sp => sp.GetRequiredService<TaskService>().DecideExtensionAsync(ext.Id, new ExtensionDecisionRequest(true, "OK"), default));
        var task = await app.Db(db => db.Tasks.AsNoTracking().FirstAsync(x => x.Id == t.Id));
        Assert.Equal(new DateOnly(2026, 10, 16), task.DueDate);
        Assert.Equal(new DateOnly(2026, 10, 9), task.OriginalDueDate);
        Assert.Equal(AcknowledgementState.Accepted, task.Acknowledgement);
        Assert.False(await app.Db(db => db.AttentionItems.AnyAsync(a => a.Type == AttentionType.ExtensionRequest && a.Status == AttentionStatus.Open)));
    }

    [Fact]
    public async Task A_comment_with_a_promise_records_a_commitment_and_resets_no_response()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await CreateAsync(app, "ceo", new CreateTaskRequest { Title = "Dealer CRM screens", PrimaryAssigneeId = app.People["dev"], DueDate = new DateOnly(2026, 10, 16) });
        await app.As("ceo", sp => sp.GetRequiredService<TaskService>().SendReminderAsync(t.Id, new ReminderRequest(), default));
        Assert.Equal(1, (await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id))).RemindersSinceResponse);

        await app.As("dev", sp => sp.GetRequiredService<TaskService>().CommentAsync(t.Id, new CommentRequest("Screens are 70% done. We will complete this by Thursday."), default));
        var c = await app.Db(db => db.Commitments.SingleAsync(x => x.TaskId == t.Id));
        Assert.Equal(new DateOnly(2026, 10, 8), c.CommitmentDate);
        Assert.Equal(app.People["dev"], c.EmployeeId);
        Assert.Equal(app.People["ceo"], c.CommittedToId);
        var task = await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id));
        Assert.Equal(0, task.RemindersSinceResponse);
        Assert.Null(task.NoResponseSince);
    }

    [Fact]
    public async Task Critical_work_needs_confirmation_before_it_is_completed()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await CreateAsync(app, "ceo", new CreateTaskRequest { Title = "Payment fix", PrimaryAssigneeId = app.People["dev"], Criticality = Criticality.Critical, DueDate = new DateOnly(2026, 10, 9) });
        await app.As("dev", sp => sp.GetRequiredService<TaskService>().CommentAsync(t.Id, new CommentRequest("I will finish this by Wednesday."), default));
        await app.As("dev", sp => sp.GetRequiredService<TaskService>().CompleteAsync(t.Id, new CompleteRequest(null, "Deployed",
            [new EvidenceInput(EvidenceKind.GitCommit, "Fix", Reference: "abc123"), new EvidenceInput(EvidenceKind.Url, "Run", "https://ci/1")]), default));
        Assert.Equal(WorkStatus.CompletionAwaitingApproval, (await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id))).Status);
        Assert.Equal(2, await app.Db(db => db.CompletionEvidence.CountAsync(x => x.TaskId == t.Id)));

        await Assert.ThrowsAsync<ForbiddenException>(() => app.As("dev", sp => sp.GetRequiredService<TaskService>().DecideApprovalAsync(t.Id, new ApprovalRequest(ApprovalAction.Confirm, null), default)));
        await app.As("ceo", sp => sp.GetRequiredService<TaskService>().DecideApprovalAsync(t.Id, new ApprovalRequest(ApprovalAction.Confirm, "Good"), default));
        var task = await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id));
        Assert.Equal(WorkStatus.Completed, task.Status);
        Assert.Null(task.NextFollowUpAt);
        Assert.Equal(CommitmentStatus.Met, (await app.Db(db => db.Commitments.SingleAsync(x => x.TaskId == t.Id))).Status);
    }

    [Fact]
    public async Task Approver_can_request_more_work()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await CreateAsync(app, "ceo", new CreateTaskRequest { Title = "Report", PrimaryAssigneeId = app.People["dev"], Criticality = Criticality.Critical });
        await app.As("dev", sp => sp.GetRequiredService<TaskService>().CompleteAsync(t.Id, new CompleteRequest(null, "Draft", null), default));
        await app.As("ceo", sp => sp.GetRequiredService<TaskService>().DecideApprovalAsync(t.Id, new ApprovalRequest(ApprovalAction.RequestMoreWork, "Add regional split"), default));
        var task = await app.Db(db => db.Tasks.FirstAsync(x => x.Id == t.Id));
        Assert.Equal(WorkStatus.InProgress, task.Status);
        Assert.Equal(ApprovalDecision.MoreWorkRequested, (await app.Db(db => db.TaskApprovals.SingleAsync(a => a.TaskId == t.Id))).Decision);
    }

    [Fact]
    public async Task Confidential_tasks_are_invisible_to_others()
    {
        await using var app = await TestApp.CreateAsync();
        var secret = await CreateAsync(app, "ceo", new CreateTaskRequest { Title = "Project Falcon", PrimaryAssigneeId = app.People["cto"], Visibility = TaskVisibility.CeoOnly, TeamId = app.TeamId });
        var teamOnly = await CreateAsync(app, "ceo", new CreateTaskRequest { Title = "Team work", PrimaryAssigneeId = app.People["lead"], Visibility = TaskVisibility.TeamOnly, TeamId = app.TeamId });

        await Assert.ThrowsAsync<NotFoundException>(() => app.As("lead", sp => sp.GetRequiredService<TaskService>().LoadAsync(secret.Id, default)));
        await Assert.ThrowsAsync<NotFoundException>(() => app.As("outsider", sp => sp.GetRequiredService<TaskService>().LoadAsync(teamOnly.Id, default)));
        Assert.Equal(teamOnly.Id, (await app.As("dev2", sp => sp.GetRequiredService<TaskService>().LoadAsync(teamOnly.Id, default))).Id);
        Assert.Equal(secret.Id, (await app.As("cto", sp => sp.GetRequiredService<TaskService>().LoadAsync(secret.Id, default))).Id);

        // The SQL filter agrees with the domain rule.
        var visibleToDev2 = await app.As("dev2", async sp =>
        {
            var db = sp.GetRequiredService<FollowUpDbContext>();
            return await db.Tasks.VisibleTo(db, sp.GetRequiredService<ActorContext>().Viewer).Select(t => t.Id).ToListAsync();
        });
        Assert.Equal([teamOnly.Id], visibleToDev2);
    }

    [Fact]
    public async Task Finish_to_start_dependencies_block_until_the_predecessor_completes()
    {
        await using var app = await TestApp.CreateAsync();
        var design = await CreateAsync(app, "ceo", new CreateTaskRequest { Title = "Design", PrimaryAssigneeId = app.People["dev"], RequiresAcknowledgement = false });
        var build = await CreateAsync(app, "ceo", new CreateTaskRequest { Title = "Build", PrimaryAssigneeId = app.People["dev2"], RequiresAcknowledgement = false, Dependencies = [new DependencyInput(design.Id)] });
        var blocked = await app.Db(db => db.Tasks.FirstAsync(x => x.Id == build.Id));
        Assert.True(blocked.IsBlocked);
        Assert.Equal($"Blocked by {design.Number}", blocked.BlockedReason);
        await Assert.ThrowsAsync<ValidationException>(() => app.As("ceo", sp => sp.GetRequiredService<TaskService>().AddDependencyAsync(design.Id, new DependencyInput(build.Id), default)));

        await app.As("dev", sp => sp.GetRequiredService<TaskService>().CompleteAsync(design.Id, new CompleteRequest(null, null, null), default));
        Assert.False((await app.Db(db => db.Tasks.FirstAsync(x => x.Id == build.Id))).IsBlocked);
    }

    [Fact]
    public async Task Audit_trail_is_append_only_and_hash_chained()
    {
        await using var app = await TestApp.CreateAsync();
        var t = await CreateAsync(app, "ceo", new CreateTaskRequest { Title = "Audited", PrimaryAssigneeId = app.People["dev"] });
        await app.As("ceo", sp => sp.GetRequiredService<TaskService>().UpdateAsync(t.Id, new UpdateTaskRequest { Priority = TaskPriority.Urgent, DueDate = new DateOnly(2026, 10, 20) }, default));
        var actions = await app.Db(db => db.AuditLogs.Where(a => a.TaskId == t.Id).Select(a => a.Action).ToListAsync());
        Assert.Contains("TaskCreated", actions);
        Assert.Contains("PriorityChanged", actions);
        Assert.Contains("DeadlineChanged", actions);
        Assert.Null(await app.As("ceo", sp => sp.GetRequiredService<AuditService>().VerifyTaskChainAsync(t.Id, default)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Db(async db =>
        {
            var entry = await db.AuditLogs.FirstAsync(a => a.TaskId == t.Id);
            entry.NewValue = "tampered";
            return await db.SaveChangesAsync();
        }));
    }
}
