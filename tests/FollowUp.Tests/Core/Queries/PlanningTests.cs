using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Core.Services;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Tests.Core.Queries;

public sealed class PlanningTests
{
    private static TemplateInput Template() => new("Month-end close", "Finance", "Close the books", TaskPriority.High, Criticality.Important,
        RequiresApproval: true, EscalationPolicyId: null, FollowUp: FollowUpSchedule.EveryDays(2), Roles: ["Owner", "Analyst"],
        Items:
        [
            new TemplateItem { Key = "prep", Title = "Prepare trial balance", AssigneeRole = "Analyst", DueOffsetWorkingDays = 0 },
            new TemplateItem { Key = "recon", Title = "Bank reconciliation", AssigneeRole = "Analyst", DueOffsetWorkingDays = 1, DependsOn = ["prep"] },
            new TemplateItem { Key = "recon-check", ParentKey = "recon", Title = "Review reconciliation", AssigneeRole = "Owner", DueOffsetWorkingDays = 2 },
            new TemplateItem { Key = "report", Title = "Management report", AssigneeRole = "Owner", DueOffsetWorkingDays = 3, DependsOn = ["recon"] },
        ],
        ApprovalFlow: [ApproverRole.Delegator]);

    [Fact]
    public async Task Template_instantiation_puts_due_dates_on_working_days_and_wires_hierarchy_and_dependencies()
    {
        await using var host = await CoreTestHost.CreateAsync();
        var org = host.Org;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
            db.Holidays.Add(new Holiday { CalendarId = org.CalendarId, Date = new DateOnly(2026, 10, 12), Name = "Test holiday", Kind = HolidayKind.Company });
            await db.SaveChangesAsync();
        }

        await using var ceo = await host.AsAsync(org.Ceo);
        var planning = ceo.ServiceProvider.GetRequiredService<PlanningService>();
        var template = await planning.SaveTemplateAsync(null, Template(), CancellationToken.None);

        // Anchor Friday 9 Oct; Sat/Sun weekend and Monday 12 Oct a holiday.
        var parent = await planning.InstantiateAsync(template.Id, new InstantiateTemplateRequest("October close", new DateOnly(2026, 10, 9),
            new Dictionary<string, Guid> { ["Owner"] = org.FinanceHead, ["Analyst"] = org.FinanceMember }, null, org.FinanceTeam, org.Finance, null), CancellationToken.None);

        var db2 = ceo.ServiceProvider.GetRequiredService<FollowUpDbContext>();
        var all = await db2.Tasks.AsNoTracking().Where(t => t.Id == parent.Id || t.RootTaskId == parent.Id).ToListAsync();
        WorkTask T(string title) => all.Single(t => t.Title == title);

        Assert.Equal("October close", T("October close").Title);
        Assert.Equal(new DateOnly(2026, 10, 15), T("October close").DueDate);              // offset 3 → Thu 15 Oct
        Assert.True(T("October close").RequiresApproval);
        Assert.Equal(template.Id, T("October close").TemplateId);
        Assert.Equal(org.FinanceHead, T("October close").PrimaryAssigneeId);

        Assert.Equal(new DateOnly(2026, 10, 9), T("Prepare trial balance").DueDate);       // offset 0 → the anchor
        Assert.Equal(new DateOnly(2026, 10, 13), T("Bank reconciliation").DueDate);        // offset 1 skips the weekend and the holiday
        Assert.Equal(new DateOnly(2026, 10, 14), T("Review reconciliation").DueDate);
        Assert.Equal(new DateOnly(2026, 10, 15), T("Management report").DueDate);
        Assert.All(all, t => Assert.True(BusinessCalendar.From(new WorkCalendar { TimeZoneId = "Asia/Kolkata", WeekendDays = "Saturday,Sunday",
            Holidays = [new Holiday { Date = new DateOnly(2026, 10, 12), Name = "h" }] }).IsWorkingDay(t.DueDate!.Value)));

        Assert.Equal(parent.Id, T("Prepare trial balance").ParentTaskId);
        Assert.Equal(T("Bank reconciliation").Id, T("Review reconciliation").ParentTaskId);
        Assert.Equal(org.FinanceMember, T("Bank reconciliation").PrimaryAssigneeId);
        Assert.Equal(org.FinanceHead, T("Management report").PrimaryAssigneeId);
        Assert.All(all, t => Assert.Equal(template.Id, t.TemplateId));

        var deps = await db2.TaskDependencies.AsNoTracking().ToListAsync();
        Assert.Contains(deps, d => d.TaskId == T("Bank reconciliation").Id && d.DependsOnTaskId == T("Prepare trial balance").Id && d.Type == DependencyType.FinishToStart);
        Assert.Contains(deps, d => d.TaskId == T("Management report").Id && d.DependsOnTaskId == T("Bank reconciliation").Id);

        // Anchored on a Saturday, a zero offset rolls to the next working day (Tuesday, after the holiday).
        var second = await planning.InstantiateAsync(template.Id, new InstantiateTemplateRequest(null, new DateOnly(2026, 10, 10),
            new Dictionary<string, Guid> { ["Owner"] = org.FinanceHead }, null, null, null, null), CancellationToken.None);
        var prep = await db2.Tasks.AsNoTracking().SingleAsync(t => t.RootTaskId == second.Id && t.Title == "Prepare trial balance");
        Assert.Equal(new DateOnly(2026, 10, 13), prep.DueDate);
        Assert.Equal(org.FinanceHead, prep.PrimaryAssigneeId);                              // unmapped role falls back to the parent's owner
    }

    [Fact]
    public async Task Template_validation_rejects_unknown_dependencies_and_cycles()
    {
        await using var host = await CoreTestHost.CreateAsync();
        await using var ceo = await host.AsAsync(host.Org.Ceo);
        var planning = ceo.ServiceProvider.GetRequiredService<PlanningService>();
        var bad = Template() with { Name = "Bad", Items = [new TemplateItem { Key = "a", Title = "A", DependsOn = ["missing"] }] };
        var ex = await Assert.ThrowsAsync<ValidationException>(() => planning.SaveTemplateAsync(null, bad, CancellationToken.None));
        Assert.Equal("item_dependency_invalid", ex.Code);
        var cycle = Template() with
        {
            Name = "Cycle",
            Items = [new TemplateItem { Key = "a", Title = "A", DependsOn = ["b"] }, new TemplateItem { Key = "b", Title = "B", DependsOn = ["a"] }],
        };
        Assert.Equal("item_dependency_cycle", (await Assert.ThrowsAsync<ValidationException>(() => planning.SaveTemplateAsync(null, cycle, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Meeting_action_items_become_tasks_with_a_two_day_follow_up()
    {
        await using var host = await CoreTestHost.CreateAsync();
        var org = host.Org;
        await using var ceo = await host.AsAsync(org.Ceo);
        var planning = ceo.ServiceProvider.GetRequiredService<PlanningService>();
        var meeting = await planning.SaveMeetingAsync(null, new MeetingInput("Weekly management meeting", TestOrg.Now.AddHours(-2), org.Ceo, "Board room", null, null,
            [org.FinanceHead, org.DevLead],
            [new ActionItemInput(null, "Cut vendor costs", "Renegotiate the courier contract", org.FinanceHead, TestOrg.Today.AddDays(5), Criticality.Important),
             new ActionItemInput(null, null, "Somebody should look at parking", null, null)]), CancellationToken.None);

        var result = await planning.CreateTasksFromActionItemsAsync(meeting.Id, new CreateActionTasksRequest(null, null), CancellationToken.None);
        var created = result.ActionItems.Single(a => a.Owner?.Id == org.FinanceHead);
        Assert.NotNull(created.TaskId);
        Assert.Equal(MeetingActionStatus.TaskCreated, created.Status);
        Assert.Null(result.ActionItems.Single(a => a.Owner is null).TaskId);

        var detail = await ceo.ServiceProvider.GetRequiredService<TaskQueryService>().GetDetailAsync(created.TaskId!.Value, CancellationToken.None);
        Assert.Equal(TaskSource.Meeting, detail.Source);
        Assert.Equal(meeting.Id, detail.MeetingId);
        Assert.Equal(FollowUpFrequency.EveryNDays, detail.FollowUp.Schedule.Frequency);
        Assert.Equal(2, detail.FollowUp.Schedule.IntervalDays);
    }
}
