using FollowUp.Core.Abstractions;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Core.Services;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Tests.Core.Queries;

public sealed class ExecutiveViewTests
{
    private static readonly DateOnly Today = TestOrg.Today;

    /// <summary>
    /// T1 critical due today · T2 due Saturday (unanswered reminder) · T3 important overdue 2 days · T4 overdue 10 days ·
    /// T5 blocked · T6 completed today · T7 draft.
    /// </summary>
    private static async Task<Dictionary<string, WorkTask>> SeedWorkbenchAsync(CoreTestHost host)
    {
        var org = host.Org;
        var t = new Dictionary<string, WorkTask>
        {
            ["T1"] = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Board deck numbers", PrimaryAssigneeId = org.FinanceHead, DueDate = Today, Criticality = Criticality.Critical }),
            ["T2"] = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Vendor payments", PrimaryAssigneeId = org.FinanceMember, DueDate = Today.AddDays(3) }),
            ["T3"] = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Audit queries", PrimaryAssigneeId = org.FinanceMember2, DueDate = Today.AddDays(-2), Criticality = Criticality.Important }),
            ["T4"] = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Server migration", PrimaryAssigneeId = org.DevMember, DueDate = Today.AddDays(-10) }),
            ["T5"] = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Bank API integration", PrimaryAssigneeId = org.DevLead }),
            ["T6"] = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Renew domain", PrimaryAssigneeId = org.DevMember, DueDate = Today.AddDays(1) }),
            ["T7"] = await host.CreateTaskAsync(new CreateTaskRequest { Title = "Idea: new office", IsDraft = true }),
        };
        await using var ceo = await host.AsAsync(org.Ceo);
        var tasks = ceo.ServiceProvider.GetRequiredService<TaskService>();
        await tasks.ChangeStatusAsync(t["T5"].Id, new StatusChangeRequest(WorkStatus.Blocked, "Waiting for the bank"), CancellationToken.None);
        await tasks.CompleteAsync(t["T6"].Id, new CompleteRequest(null, "Renewed", null), CancellationToken.None);
        await tasks.SendReminderAsync(t["T2"].Id, new ReminderRequest(), CancellationToken.None);
        return t;
    }

    [Fact]
    public async Task Workbench_tiles_count_the_fixture_and_sections_rank_by_severity()
    {
        await using var host = await CoreTestHost.CreateAsync();
        var t = await SeedWorkbenchAsync(host);
        await using var ceo = await host.AsAsync(host.Org.Ceo);
        var wb = await ceo.ServiceProvider.GetRequiredService<DashboardService>().CeoWorkbenchAsync(CancellationToken.None);
        int Tile(string key) => wb.Tiles.Single(x => x.Key == key).Value;

        Assert.Equal(5, Tile("open"));
        Assert.Equal(1, Tile("critical"));
        Assert.Equal(1, Tile("due-today"));
        Assert.Equal(2, Tile("due-this-week"));
        Assert.Equal(2, Tile("overdue"));
        Assert.Equal(1, Tile("severely-overdue"));
        Assert.Equal(1, Tile("no-response"));
        Assert.Equal(1, Tile("blocked"));
        Assert.Equal(1, Tile("completed-this-week"));
        Assert.Equal(0, Tile("awaiting-approval"));
        Assert.Equal(13, wb.Tiles.Count);
        Assert.Equal("/tasks", wb.Tiles.Single(x => x.Key == "overdue").Drilldown.Route);
        Assert.Equal("true", wb.Tiles.Single(x => x.Key == "overdue").Drilldown.Query["overdue"]);

        var overdue = wb.Sections.Single(s => s.Key == "overdue");
        Assert.Equal([t["T4"].Id, t["T3"].Id], overdue.Tasks.Select(x => x.Id).ToList());   // 10 days × normal outranks 2 days × important
        Assert.Contains(wb.Sections.Single(s => s.Key == "upcoming").Tasks, x => x.Id == t["T1"].Id);
        Assert.Contains(wb.Sections.Single(s => s.Key == "completed").Tasks, x => x.Id == t["T6"].Id);
        Assert.Contains(wb.Sections.Single(s => s.Key == "no-response").Tasks, x => x.Id == t["T2"].Id);
        Assert.StartsWith("Good morning, Asha", wb.Greeting);
        Assert.Equal("5 tasks are active.", wb.Summary.Points[0].Text);

        // The same numbers through the task list drill-downs.
        var q = ceo.ServiceProvider.GetRequiredService<TaskQueryService>();
        Assert.Equal(2, (await q.QueryAsync(new TaskFilter { Overdue = true }, CancellationToken.None)).Total);
        Assert.Equal(5, (await q.QueryAsync(new TaskFilter { Open = true }, CancellationToken.None)).Total);
    }

    [Fact]
    public async Task Ask_answers_finance_tasks_overdue_by_more_than_five_days()
    {
        await using var host = await CoreTestHost.CreateAsync(s => s.AddSingleton<INaturalLanguageService, FakeLanguageService>());
        var org = host.Org;
        foreach (var days in new[] { 6, 8, 12 })
            await host.CreateTaskAsync(new CreateTaskRequest { Title = $"Finance item {days}", PrimaryAssigneeId = org.FinanceMember, DepartmentId = org.Finance, DueDate = Today.AddDays(-days) });
        await host.CreateTaskAsync(new CreateTaskRequest { Title = "Finance item recent", PrimaryAssigneeId = org.FinanceMember, DepartmentId = org.Finance, DueDate = Today.AddDays(-2) });
        await host.CreateTaskAsync(new CreateTaskRequest { Title = "Dev item late", PrimaryAssigneeId = org.DevMember, DepartmentId = org.Development, DueDate = Today.AddDays(-20) });

        await using var ceo = await host.AsAsync(org.Ceo);
        var answer = await ceo.ServiceProvider.GetRequiredService<AskService>().AskAsync("Show Finance tasks overdue by more than five days", CancellationToken.None);

        Assert.Equal(QuestionIntent.Overdue, answer.Query.Intent);
        Assert.Equal("3 Finance tasks are overdue by more than 5 days.", answer.Answer);
        Assert.Equal(3, answer.Tasks.Count);
        Assert.All(answer.Tasks, t => Assert.Equal("Finance", t.DepartmentName));
        Assert.Equal("/tasks", answer.Drilldown!.Route);
        Assert.Equal(org.Finance.ToString(), answer.Drilldown.Query["departmentId"]);
        Assert.Equal("6", answer.Drilldown.Query["minDaysOverdue"]);
        Assert.Equal("Fake", answer.Provider);
    }

    [Fact]
    public async Task Parse_command_resolves_names_and_shows_workload()
    {
        await using var host = await CoreTestHost.CreateAsync(s => s.AddSingleton<INaturalLanguageService, FakeLanguageService>());
        await using var ceo = await host.AsAsync(host.Org.Ceo);
        var draft = await ceo.ServiceProvider.GetRequiredService<AskService>().ParseCommandAsync("Ask Raj to send the GST report by Friday", false, CancellationToken.None);
        Assert.Equal("Raj Patel", draft.PrimaryAssignee?.Name);
        Assert.Equal("Finance", draft.Department?.Name);
        Assert.Single(draft.Workload);
        Assert.StartsWith("Due Friday 9 Oct 2026", draft.DueRuleText);
        Assert.Equal("Every 2 days at 10:00 until completed", draft.FollowUpText);
    }

    [Fact]
    public async Task Daily_digest_is_queued_once_per_day()
    {
        await using var host = await CoreTestHost.CreateAsync();
        await SeedWorkbenchAsync(host);
        await using (var ceo = await host.AsAsync(host.Org.Ceo))
        {
            var digest = ceo.ServiceProvider.GetRequiredService<DigestService>();
            var preview = await digest.DigestAsync(CancellationToken.None);
            Assert.Equal(5, preview.Open);
            Assert.Equal(2, preview.Overdue);
            var first = await digest.SendDailyDigestAsync(CancellationToken.None);
            Assert.Equal(2, first);                                          // CEO: email + in-app
            Assert.Equal(0, await digest.SendDailyDigestAsync(CancellationToken.None));
        }
        await using (var scope = await host.AsAsync(host.Org.Ceo))
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<DigestService>().SendDailyDigestAsync(CancellationToken.None));
            var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
            var rows = await db.Notifications.Where(n => n.Category == NotificationCategory.Digest).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, n => Assert.StartsWith("digest:2026-10-07:", n.IdempotencyKey));
            Assert.Contains("Overdue: 2", rows.Single(n => n.Channel == NotificationChannel.Email).Body);
        }
    }

    [Fact]
    public async Task Weekly_review_team_dashboard_and_projects_are_built()
    {
        await using var host = await CoreTestHost.CreateAsync();
        var t = await SeedWorkbenchAsync(host);
        await using var ceo = await host.AsAsync(host.Org.Ceo);
        var review = await ceo.ServiceProvider.GetRequiredService<DigestService>().WeeklyReviewAsync(Today, CancellationToken.None);
        Assert.Equal(new DateOnly(2026, 10, 5), review.WeekStart);
        Assert.Contains(review.Sections.Single(s => s.Key == "completed").Tasks, x => x.Id == t["T6"].Id);
        Assert.Contains(review.Sections.Single(s => s.Key == "delayed").Tasks, x => x.Id == t["T4"].Id);
        Assert.Equal(2, review.Teams.Count);

        await using var lead = await host.AsAsync(host.Org.FinanceHead);
        var dash = lead.ServiceProvider.GetRequiredService<DashboardService>();
        Assert.Contains(await dash.TeamsAsync(CancellationToken.None), x => x.Id == host.Org.FinanceTeam);
        var team = await dash.TeamDashboardAsync(host.Org.FinanceTeam, CancellationToken.None);
        Assert.Equal(3, team.Members.Count);
        Assert.Equal(1, team.Members.Single(m => m.Person.Id == host.Org.FinanceMember2).Overdue);
        await Assert.ThrowsAsync<FollowUp.Core.Common.ForbiddenException>(() => dash.TeamDashboardAsync(host.Org.DevTeam, CancellationToken.None));

        var planning = ceo.ServiceProvider.GetRequiredService<PlanningService>();
        var project = await planning.SaveProjectAsync(null, new ProjectInput("ERP", "ERP rollout", null, host.Org.DevLead, host.Org.Development, null, null), CancellationToken.None);
        Assert.Equal("Not Started", project.Status);
        await ceo.ServiceProvider.GetRequiredService<TaskService>().UpdateAsync(t["T4"].Id, new UpdateTaskRequest { ProjectId = project.Id }, CancellationToken.None);
        var detail = await ceo.ServiceProvider.GetRequiredService<DashboardService>().ProjectDetailAsync(project.Id, CancellationToken.None);
        Assert.Equal("Off Track", detail.Summary.Status);
        Assert.Equal(1, detail.Summary.OverdueTasks);
    }

    /// <summary>Deterministic stand-in for the AI natural-language port.</summary>
    private sealed class FakeLanguageService : INaturalLanguageService
    {
        public string ProviderName => "Fake";

        public Task<TaskDraft> ParseTaskCommandAsync(string command, DirectorySnapshot directory, DateTime nowUtc, BusinessCalendar calendar, CancellationToken ct)
        {
            var raj = directory.People.Single(p => p.Aliases.Contains("Raj"));
            return Task.FromResult(new TaskDraft
            {
                Title = "Send the GST report", PrimaryAssigneeId = raj.Id, DepartmentId = raj.DepartmentId, DueDate = new DateOnly(2026, 10, 9),
                FollowUp = FollowUpSchedule.EveryDays(2), Confidence = 0.9,
            });
        }

        public Task<QuestionQuery> ParseQuestionAsync(string question, DirectorySnapshot directory, DateTime nowUtc, BusinessCalendar calendar, CancellationToken ct) =>
            Task.FromResult(new QuestionQuery
            {
                Intent = QuestionIntent.Overdue,
                DepartmentId = directory.Departments.Single(d => d.Name == "Finance").Id,
                MinDaysOverdue = 6,
                Confidence = 0.9,
                Interpretation = "Finance tasks overdue by more than 5 days",
            });

        public Task<string?> NarrateAsync(string heading, IReadOnlyList<string> points, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
