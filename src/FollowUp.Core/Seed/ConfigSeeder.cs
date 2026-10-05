using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FollowUp.Core.Seed;

/// <summary>
/// Idempotent configuration seed applied on every start: default working calendar with holidays,
/// task categories, escalation ladders (spec §34) and the standard task templates (spec §51).
/// </summary>
public static class ConfigSeeder
{
    public static readonly Guid DefaultCalendarId = Ids.Deterministic("calendar", "india");
    public static readonly Guid UaeCalendarId = Ids.Deterministic("calendar", "uae");
    public static readonly Guid StandardPolicyId = Ids.Deterministic("policy", "standard");
    public static readonly Guid CriticalPolicyId = Ids.Deterministic("policy", "critical");
    public static readonly Guid InformationalPolicyId = Ids.Deterministic("policy", "informational");

    public static async Task RunAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;

        if (!await db.WorkCalendars.AnyAsync(ct))
        {
            var india = new WorkCalendar
            {
                Id = DefaultCalendarId, Name = "India — Head Office", Region = "IN", TimeZoneId = "Asia/Kolkata", WeekendDays = "Saturday,Sunday",
                WorkdayStart = new TimeOnly(9, 30), WorkdayEnd = new TimeOnly(18, 30), IsDefault = true, CreatedAt = now, UpdatedAt = now,
            };
            foreach (var (date, name, kind) in IndiaHolidays())
                india.Holidays.Add(new Holiday { CalendarId = india.Id, Date = date, Name = name, Kind = kind });
            var uae = new WorkCalendar
            {
                Id = UaeCalendarId, Name = "UAE — Dubai Office", Region = "AE", TimeZoneId = "Asia/Dubai", WeekendDays = "Saturday,Sunday",
                WorkdayStart = new TimeOnly(9, 0), WorkdayEnd = new TimeOnly(18, 0), CreatedAt = now, UpdatedAt = now,
            };
            foreach (var (date, name) in new[] { (new DateOnly(2026, 12, 2), "UAE National Day"), (new DateOnly(2026, 12, 3), "UAE National Day holiday"), (new DateOnly(2027, 1, 1), "New Year's Day") })
                uae.Holidays.Add(new Holiday { CalendarId = uae.Id, Date = date, Name = name, Kind = HolidayKind.Public });
            db.WorkCalendars.AddRange(india, uae);
            logger.LogInformation("Seeded working calendars");
        }

        foreach (var (name, color) in new[] { ("Strategic", "#4f46e5"), ("Operational", "#0891b2"), ("Compliance", "#b45309"), ("Customer", "#16a34a"), ("Finance", "#9333ea"), ("People", "#db2777"), ("Technology", "#2563eb") })
            if (!await db.TaskCategories.AnyAsync(c => c.Name == name, ct))
                db.TaskCategories.Add(new TaskCategory { Id = Ids.Deterministic("category", name), Name = name, Color = color });

        if (!await db.EscalationPolicies.AnyAsync(ct))
        {
            db.EscalationPolicies.AddRange(StandardPolicy(now), CriticalPolicy(now), InformationalPolicy(now));
            logger.LogInformation("Seeded escalation policies");
        }

        foreach (var template in Templates(now))
            if (!await db.TaskTemplates.AnyAsync(t => t.Name == template.Name, ct)) db.TaskTemplates.Add(template);

        await db.SaveChangesAsync(ct);
        scope.ServiceProvider.GetRequiredService<Common.CalendarProvider>().Invalidate();
    }

    private static IEnumerable<(DateOnly, string, HolidayKind)> IndiaHolidays() =>
    [
        (new DateOnly(2026, 1, 26), "Republic Day", HolidayKind.Public),
        (new DateOnly(2026, 3, 4), "Holi", HolidayKind.Public),
        (new DateOnly(2026, 4, 3), "Good Friday", HolidayKind.Public),
        (new DateOnly(2026, 5, 1), "Labour Day", HolidayKind.Regional),
        (new DateOnly(2026, 8, 15), "Independence Day", HolidayKind.Public),
        (new DateOnly(2026, 8, 26), "Onam", HolidayKind.Regional),
        (new DateOnly(2026, 10, 2), "Gandhi Jayanti", HolidayKind.Public),
        (new DateOnly(2026, 10, 20), "Dussehra", HolidayKind.Public),
        (new DateOnly(2026, 11, 9), "Diwali (company holiday)", HolidayKind.Company),
        (new DateOnly(2026, 12, 25), "Christmas", HolidayKind.Public),
        (new DateOnly(2027, 1, 1), "New Year (company holiday)", HolidayKind.Company),
        (new DateOnly(2027, 1, 26), "Republic Day", HolidayKind.Public),
        (new DateOnly(2027, 3, 22), "Holi", HolidayKind.Public),
    ];

    private static EscalationStep Step(int order, EscalationTrigger trigger, double threshold, ThresholdUnit unit, EscalationTarget targets, int level, bool ceo = false) =>
        new() { Order = order, Trigger = trigger, Threshold = threshold, Unit = unit, Targets = targets, Level = level, RaiseCeoAttention = ceo, Channels = NotifyChannels.Email | NotifyChannels.Teams };

    /// <summary>The ladder from spec §34: due date → assignee; +1 day → team leader; +3 days → department head; +5 days → CEO.</summary>
    private static EscalationPolicy StandardPolicy(DateTime now)
    {
        var p = new EscalationPolicy { Id = StandardPolicyId, Name = "Standard ladder", Description = "Due date → assignee; 1 day overdue → + team leader; 3 days → + department head; 5 days → CEO.", AppliesTo = "Normal,Important", IsDefault = true, CreatedAt = now, UpdatedAt = now };
        p.Steps.AddRange(
        [
            Step(1, EscalationTrigger.OnDue, 0, ThresholdUnit.Days, EscalationTarget.Assignee, 1),
            Step(2, EscalationTrigger.Overdue, 1, ThresholdUnit.Days, EscalationTarget.Assignee | EscalationTarget.TeamLeader, 2),
            Step(3, EscalationTrigger.Overdue, 3, ThresholdUnit.Days, EscalationTarget.Assignee | EscalationTarget.TeamLeader | EscalationTarget.DepartmentHead, 3),
            Step(4, EscalationTrigger.Overdue, 5, ThresholdUnit.Days, EscalationTarget.Assignee | EscalationTarget.TeamLeader | EscalationTarget.DepartmentHead | EscalationTarget.Ceo, 4, true),
            Step(5, EscalationTrigger.NoResponse, 3, ThresholdUnit.Days, EscalationTarget.TeamLeader | EscalationTarget.Delegator, 2),
            Step(6, EscalationTrigger.AcknowledgementPending, 48, ThresholdUnit.Hours, EscalationTarget.Assignee | EscalationTarget.TeamLeader, 2),
            Step(7, EscalationTrigger.CommitmentMissed, 0, ThresholdUnit.Hours, EscalationTarget.Delegator | EscalationTarget.TeamLeader, 2),
        ]);
        foreach (var s in p.Steps) s.PolicyId = p.Id;
        return p;
    }

    /// <summary>Critical work escalates much faster (spec §34).</summary>
    private static EscalationPolicy CriticalPolicy(DateTime now)
    {
        var p = new EscalationPolicy { Id = CriticalPolicyId, Name = "Critical fast-track", Description = "Hours, not days: CEO alerted one day after a missed critical deadline or two days of silence.", AppliesTo = "Critical,BusinessCritical", CreatedAt = now, UpdatedAt = now };
        p.Steps.AddRange(
        [
            Step(1, EscalationTrigger.BeforeDue, 1, ThresholdUnit.Days, EscalationTarget.Assignee, 1),
            Step(2, EscalationTrigger.OnDue, 0, ThresholdUnit.Days, EscalationTarget.Assignee | EscalationTarget.TeamLeader, 1),
            Step(3, EscalationTrigger.Overdue, 4, ThresholdUnit.Hours, EscalationTarget.Assignee | EscalationTarget.TeamLeader | EscalationTarget.AccountableManager | EscalationTarget.DepartmentHead, 3),
            Step(4, EscalationTrigger.Overdue, 1, ThresholdUnit.Days, EscalationTarget.Assignee | EscalationTarget.DepartmentHead | EscalationTarget.Delegator | EscalationTarget.Ceo, 5, true),
            Step(5, EscalationTrigger.NoResponse, 1, ThresholdUnit.Days, EscalationTarget.TeamLeader | EscalationTarget.DepartmentHead, 2),
            Step(6, EscalationTrigger.NoResponse, 2, ThresholdUnit.Days, EscalationTarget.TeamLeader | EscalationTarget.DepartmentHead | EscalationTarget.Ceo, 4, true),
            Step(7, EscalationTrigger.AcknowledgementPending, 8, ThresholdUnit.Hours, EscalationTarget.TeamLeader | EscalationTarget.Delegator, 2),
            Step(8, EscalationTrigger.CommitmentMissed, 0, ThresholdUnit.Hours, EscalationTarget.Delegator | EscalationTarget.DepartmentHead, 3),
            Step(9, EscalationTrigger.Blocked, 2, ThresholdUnit.Days, EscalationTarget.DepartmentHead | EscalationTarget.Delegator, 3),
        ]);
        foreach (var s in p.Steps) s.PolicyId = p.Id;
        return p;
    }

    private static EscalationPolicy InformationalPolicy(DateTime now)
    {
        var p = new EscalationPolicy { Id = InformationalPolicyId, Name = "Informational", Description = "A single due-date notice; never escalated.", AppliesTo = "Informational", CreatedAt = now, UpdatedAt = now };
        p.Steps.Add(Step(1, EscalationTrigger.OnDue, 0, ThresholdUnit.Days, EscalationTarget.Assignee, 1));
        p.Steps[0].Channels = NotifyChannels.Email;
        p.Steps[0].PolicyId = p.Id;
        return p;
    }

    private static TemplateItem Item(string key, string title, int dueOffset, string? role = null, string? parent = null, params string[] dependsOn) =>
        new() { Key = key, Title = title, DueOffsetWorkingDays = dueOffset, AssigneeRole = role, ParentKey = parent, DependsOn = [.. dependsOn] };

    private static IEnumerable<TaskTemplate> Templates(DateTime now)
    {
        TaskTemplate T(string name, string category, string description, Criticality crit, string[] roles, ApproverRole[] flow, params TemplateItem[] items) => new()
        {
            Id = Ids.Deterministic("template", name), Name = name, Category = category, Description = description, Criticality = crit,
            Priority = crit >= Criticality.Critical ? TaskPriority.High : TaskPriority.Normal, RequiresApproval = flow.Length > 0,
            FollowUp = FollowUpSchedule.EveryDays(2), Roles = [.. roles], Items = [.. items], ApprovalFlow = [.. flow], CreatedAt = now, UpdatedAt = now,
        };

        yield return T("Monthly Closing", "Finance", "Month-end close: reconciliations, accruals, management accounts.", Criticality.Important,
            ["Finance Lead", "Accounts", "Collections"], [ApproverRole.DepartmentHead],
            Item("bank", "Bank reconciliation", 2, "Accounts"), Item("ar", "Receivables ageing & provisions", 3, "Collections"),
            Item("ap", "Payables cut-off & accruals", 3, "Accounts"), Item("gst", "GST reconciliation (GSTR-2B vs books)", 4, "Accounts"),
            Item("mis", "Management accounts & MIS pack", 6, "Finance Lead", null, "bank", "ar", "ap", "gst"));
        yield return T("Product Release", "Technology", "Release train from code freeze to production rollout.", Criticality.Critical,
            ["Release Manager", "Developer", "QA Lead", "Support Lead"], [ApproverRole.TeamLeader, ApproverRole.Delegator],
            Item("freeze", "Code freeze & branch cut", 0, "Release Manager"), Item("qa", "Regression testing", 4, "QA Lead", null, "freeze"),
            Item("notes", "Release notes", 4, "Release Manager"), Item("uat", "UAT sign-off", 6, "QA Lead", null, "qa"),
            Item("deploy", "Production deployment", 7, "Developer", null, "uat"), Item("support", "Support briefing", 7, "Support Lead", null, "notes"));
        yield return T("Customer Go-Live", "Customer", "Implementation go-live checklist for a customer.", Criticality.Important,
            ["Implementation Lead", "Consultant", "Support Lead"], [ApproverRole.Delegator],
            Item("plan", "Go-live plan & cut-over schedule", 1, "Implementation Lead"), Item("migrate", "Master & opening balance migration", 5, "Consultant", null, "plan"),
            Item("train", "End-user training", 6, "Consultant"), Item("uat", "Customer UAT sign-off", 8, "Implementation Lead", null, "migrate", "train"),
            Item("live", "Go-live & hypercare", 10, "Implementation Lead", null, "uat"), Item("handover", "Handover to support", 12, "Support Lead", null, "live"));
        yield return T("Hiring", "People", "Requisition to joining.", Criticality.Normal,
            ["Hiring Manager", "HR"], [],
            Item("jd", "Approve job description", 1, "Hiring Manager"), Item("source", "Source & screen candidates", 8, "HR", null, "jd"),
            Item("interview", "Interviews", 12, "Hiring Manager", null, "source"), Item("offer", "Offer & negotiation", 15, "HR", null, "interview"),
            Item("onboard", "Onboarding plan", 20, "HR", null, "offer"));
        yield return T("Server Deployment", "Technology", "Provisioning and hardening a new server.", Criticality.Important,
            ["Infrastructure", "Security"], [ApproverRole.TeamLeader],
            Item("provision", "Provision VM & storage", 1, "Infrastructure"), Item("harden", "OS hardening & patching", 2, "Security", null, "provision"),
            Item("backup", "Backup & monitoring setup", 3, "Infrastructure", null, "provision"), Item("dr", "DR test", 5, "Infrastructure", null, "backup"));
        yield return T("Statutory Compliance", "Compliance", "Statutory filing with evidence of submission.", Criticality.Critical,
            ["Compliance Owner", "Reviewer"], [ApproverRole.DepartmentHead],
            Item("data", "Collect data & reconcile", 2, "Compliance Owner"), Item("review", "Review computation", 3, "Reviewer", null, "data"),
            Item("file", "File return & save acknowledgement", 4, "Compliance Owner", null, "review"));
        yield return T("Customer Escalation", "Customer", "Structured response to a customer escalation.", Criticality.BusinessCritical,
            ["Account Owner", "Technical Lead", "Support Lead"], [ApproverRole.Ceo],
            Item("ack", "Acknowledge the customer within 2 hours", 0, "Account Owner"), Item("rca", "Root-cause analysis", 1, "Technical Lead"),
            Item("fix", "Fix & verify", 2, "Technical Lead", null, "rca"), Item("comms", "Customer update & closure call", 3, "Account Owner", null, "fix"),
            Item("prevent", "Preventive actions", 5, "Support Lead", null, "rca"));
    }
}
