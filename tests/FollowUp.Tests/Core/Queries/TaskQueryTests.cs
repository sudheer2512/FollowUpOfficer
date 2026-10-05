using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Core.Services;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Tests.Core.Queries;

public sealed class TaskQueryTests
{
    private static readonly DateOnly Today = TestOrg.Today;

    [Fact]
    public async Task Team_member_cannot_see_a_CeoOnly_task_but_sees_TeamOnly_work_of_their_team()
    {
        await using var host = await CoreTestHost.CreateAsync();
        var org = host.Org;
        var secret = await host.CreateTaskAsync(new CreateTaskRequest
        {
            Title = "Confidential acquisition review", PrimaryAssigneeId = org.FinanceHead, TeamId = org.FinanceTeam, Visibility = TaskVisibility.CeoOnly,
        });
        var teamTask = await host.CreateTaskAsync(new CreateTaskRequest
        {
            Title = "Close the October books", PrimaryAssigneeId = org.FinanceHead, TeamId = org.FinanceTeam, Visibility = TaskVisibility.TeamOnly,
        });

        await using var member = await host.AsAsync(org.FinanceMember);
        var queries = member.ServiceProvider.GetRequiredService<TaskQueryService>();
        var page = await queries.QueryAsync(new TaskFilter(), CancellationToken.None);
        Assert.DoesNotContain(page.Items, t => t.Id == secret.Id);
        Assert.Contains(page.Items, t => t.Id == teamTask.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => queries.GetDetailAsync(secret.Id, CancellationToken.None));
        var search = await queries.SearchAsync("acquisition", CancellationToken.None);
        Assert.DoesNotContain(search.Hits, h => h.TaskId == secret.Id || h.Id == secret.Id);

        // The assignee and the CEO do see it.
        await using var head = await host.AsAsync(org.FinanceHead);
        Assert.Equal(secret.Id, (await head.ServiceProvider.GetRequiredService<TaskQueryService>().GetDetailAsync(secret.Id, CancellationToken.None)).Summary.Id);
    }

    [Fact]
    public async Task Filters_buckets_text_and_scope_select_the_expected_tasks()
    {
        await using var host = await CoreTestHost.CreateAsync();
        var org = host.Org;
        var dueToday = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Payroll approval", PrimaryAssigneeId = org.FinanceMember, DueDate = Today, Criticality = Criticality.Critical });
        var dueSoon = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Vendor reconciliation", PrimaryAssigneeId = org.FinanceMember, DueDate = Today.AddDays(2) });
        var overdue = await host.CreateTaskAsync(new CreateTaskRequest { Title = "GST filing", PrimaryAssigneeId = org.FinanceMember2, DueDate = Today.AddDays(-2), DepartmentId = org.Finance });
        var severe = await host.CreateTaskAsync(new CreateTaskRequest { Title = "API credentials from bank", PrimaryAssigneeId = org.DevMember, DueDate = Today.AddDays(-10) });
        var later = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Annual plan", PrimaryAssigneeId = org.DevLead, DueDate = Today.AddDays(30) });

        await using var ceo = await host.AsAsync(org.Ceo);
        var q = ceo.ServiceProvider.GetRequiredService<TaskQueryService>();
        async Task<List<Guid>> Ids(TaskFilter f) => (await q.QueryAsync(f, CancellationToken.None)).Items.Select(i => i.Id).ToList();

        Assert.Equal([dueToday.Id], await Ids(new TaskFilter { Bucket = DeadlineBucket.DueToday }));
        Assert.Equal([dueToday.Id, dueSoon.Id], await Ids(new TaskFilter { Bucket = DeadlineBucket.DueThisWeek, Sort = "due" }));
        Assert.Equal([overdue.Id], await Ids(new TaskFilter { Bucket = DeadlineBucket.Overdue }));
        Assert.Equal([severe.Id], await Ids(new TaskFilter { Bucket = DeadlineBucket.SeverelyOverdue }));
        Assert.Equal([later.Id], await Ids(new TaskFilter { Bucket = DeadlineBucket.Later }));
        Assert.Equal([severe.Id, overdue.Id], await Ids(new TaskFilter { Overdue = true, Sort = "due" }));
        Assert.Equal([severe.Id], await Ids(new TaskFilter { MinDaysOverdue = 5 }));
        Assert.Equal([dueToday.Id], await Ids(new TaskFilter { MinCriticality = Criticality.Critical }));
        Assert.Equal([severe.Id], await Ids(new TaskFilter { Text = "credentials" }));
        Assert.Equal([dueToday.Id], await Ids(new TaskFilter { Text = dueToday.Number }));
        Assert.Equal([overdue.Id], await Ids(new TaskFilter { Text = "meera" }));      // assignee name
        Assert.Equal(5, (await q.QueryAsync(new TaskFilter { Scope = "delegated" }, CancellationToken.None)).Total);
        Assert.Equal([overdue.Id, dueToday.Id, dueSoon.Id], await Ids(new TaskFilter { TeamId = org.FinanceTeam, Sort = "due" }));

        var row = (await q.QueryAsync(new TaskFilter { Text = "GST" }, CancellationToken.None)).Items.Single();
        Assert.True(row.IsOverdue);
        Assert.Equal(2, row.DaysOverdue);
        Assert.Equal("Overdue", row.DisplayStatus);
        Assert.Equal(DeadlineBucket.Overdue, row.Bucket);
    }

    [Fact]
    public async Task Detail_merges_a_chronological_timeline_and_both_dependency_directions()
    {
        await using var host = await CoreTestHost.CreateAsync();
        var org = host.Org;
        var predecessor = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Get API credentials", PrimaryAssigneeId = org.DevMember, DueDate = Today.AddDays(1) });
        var task = await host.CreateTaskAsync(new CreateTaskRequest
        {
            Title = "Integrate payment gateway", PrimaryAssigneeId = org.DevLead, DueDate = Today.AddDays(5), Criticality = Criticality.Important,
            Dependencies = [new DependencyInput(predecessor.Id)],
        });
        await using (var lead = await host.AsAsync(org.DevLead))
        {
            var tasks = lead.ServiceProvider.GetRequiredService<TaskService>();
            await tasks.AcknowledgeAsync(task.Id, new AcknowledgeRequest(AcknowledgementResponse.Accept, null, null), CancellationToken.None);
            host.Clock.Advance(TimeSpan.FromMinutes(5));
            await tasks.CommentAsync(task.Id, new CommentRequest("Waiting for the bank to issue sandbox keys."), CancellationToken.None);
        }
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
            db.TaskFollowUps.Add(new TaskFollowUpLog
            {
                TaskId = task.Id, ScheduledAt = host.Clock.UtcNow, ProcessedAt = host.Clock.UtcNow, Action = FollowUpAction.Skip,
                Kind = FollowUpKind.None, Reason = "Update received 3h ago via email — no reminder needed",
            });
            await db.SaveChangesAsync();
        }

        await using var ceo = await host.AsAsync(org.Ceo);
        var detail = await ceo.ServiceProvider.GetRequiredService<TaskQueryService>().GetDetailAsync(task.Id, CancellationToken.None);

        Assert.Equal(detail.Timeline.OrderByDescending(t => t.At).Select(t => t.At), detail.Timeline.Select(t => t.At));
        Assert.Equal("followup", detail.Timeline[0].Kind);
        Assert.StartsWith("Reminder skipped: update received 3h ago", detail.Timeline[0].Title);
        Assert.Contains(detail.Timeline, t => t.Kind == "comment" && t.Body!.Contains("sandbox keys"));
        Assert.Contains(detail.Timeline, t => t.Kind == "acknowledgement");
        Assert.Contains(detail.Timeline, t => t.Kind == "status");
        Assert.Contains(detail.Timeline, t => t.Kind == "delegation");
        Assert.Contains(detail.Timeline, t => t.Kind == "created");

        var dep = Assert.Single(detail.Dependencies);
        Assert.Equal("DependsOn", dep.Direction);
        Assert.True(dep.IsBlocking);
        Assert.Equal(predecessor.Number, dep.Number);
        Assert.True(detail.Summary.IsBlocked);
        Assert.Contains(predecessor.Number, detail.Summary.BlockedReason);

        var predecessorDetail = await ceo.ServiceProvider.GetRequiredService<TaskQueryService>().GetDetailAsync(predecessor.Id, CancellationToken.None);
        var blocks = Assert.Single(predecessorDetail.Dependencies);
        Assert.Equal("Blocks", blocks.Direction);
        Assert.Equal(task.Id, blocks.TaskId);
        Assert.True(detail.Permissions.CanManage);
    }

    [Fact]
    public async Task Subtask_tree_and_workload_are_reported()
    {
        await using var host = await CoreTestHost.CreateAsync();
        var org = host.Org;
        var parent = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Launch customer portal", PrimaryAssigneeId = org.DevLead, DueDate = Today.AddDays(10) });
        var child = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Build login page", PrimaryAssigneeId = org.DevMember, ParentTaskId = parent.Id, DueDate = Today.AddDays(3) });
        await host.CreateTaskAsync(new CreateTaskRequest { Title = "Write tests for login", PrimaryAssigneeId = org.DevMember, ParentTaskId = child.Id, DueDate = Today.AddDays(-1) });

        await using var ceo = await host.AsAsync(org.Ceo);
        var q = ceo.ServiceProvider.GetRequiredService<TaskQueryService>();
        var detail = await q.GetDetailAsync(parent.Id, CancellationToken.None);
        var node = Assert.Single(detail.Subtasks);
        Assert.Equal(child.Id, node.Id);
        Assert.Equal(1, node.Depth);
        var grandChild = Assert.Single(node.Children);
        Assert.Equal(2, grandChild.Depth);
        Assert.Equal("Overdue", grandChild.DisplayStatus);

        var load = Assert.Single(await q.WorkloadAsync([org.DevMember], null, CancellationToken.None));
        Assert.Equal(2, load.OpenTasks);
        Assert.Equal(1, load.Overdue);
        Assert.Equal("Light", load.Load);
        var team = await q.WorkloadAsync(null, org.DevTeam, CancellationToken.None);
        Assert.Equal(2, team.Count);
    }
}
