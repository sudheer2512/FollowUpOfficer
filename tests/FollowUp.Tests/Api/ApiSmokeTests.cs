using System.Net;
using System.Net.Http.Json;
using FollowUp.Core.Contracts;
using FollowUp.Domain;
using FollowUp.Tests.Core.Queries;

namespace FollowUp.Tests.Api;

/// <summary>Walks every documented route once with realistic data so wiring and query translation errors surface.</summary>
public sealed class ApiSmokeTests
{
    private static readonly TestOrg Org = TestOrg.Default;

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var response = await call;
        if (!response.IsSuccessStatusCode)
            Assert.Fail($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(ApiFactory.Json))!;
    }

    private static async Task Ok(Task<HttpResponseMessage> call)
    {
        var response = await call;
        if (!response.IsSuccessStatusCode)
            Assert.Fail($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Executive_and_task_routes_respond()
    {
        await using var factory = new ApiFactory();
        var ceo = await factory.SignInAsync("ceo");
        var raj = await factory.SignInAsync("raj");

        var critical = await Ok<TaskDetail>(ceo.PostAsJsonAsync("/api/tasks", new
        {
            title = "Quarterly GST reconciliation", description = "Match the GST portal with the books", primaryAssigneeId = Org.FinanceMember,
            departmentId = Org.Finance, criticality = "Critical", dueDate = "2026-10-01", tags = new[] { "gst", "compliance" },
            dependencies = new[] { new { externalDescription = "API credentials", externalParty = "HDFC Bank" } },
        }));
        var normal = await Ok<TaskDetail>(ceo.PostAsJsonAsync("/api/tasks", new { title = "Update vendor master", primaryAssigneeId = Org.FinanceMember2, dueDate = "2026-10-09" }));
        var sub = await Ok<TaskDetail>(ceo.PostAsJsonAsync($"/api/tasks/{normal.Summary.Id}/subtasks", new { title = "Collect PAN copies", primaryAssigneeId = Org.FinanceMember2 }));
        Assert.Equal(normal.Summary.Id, sub.Summary.ParentTaskId);
        var id = critical.Summary.Id;

        await Ok<TaskDetail>(ceo.PutAsJsonAsync($"/api/tasks/{id}", new { priority = "High", changeReason = "Escalated by auditors" }));
        await Ok<TaskDetail>(ceo.PostAsJsonAsync($"/api/tasks/{id}/assignees", new { employeeId = Org.FinanceHead, role = "Supporting" }));
        await Ok(ceo.PostAsync($"/api/tasks/{id}/followers/{Org.DevLead}", null));
        await Ok(ceo.DeleteAsync($"/api/tasks/{id}/followers/{Org.DevLead}"));
        await Ok<TaskDetail>(raj.PostAsJsonAsync($"/api/tasks/{id}/acknowledge", new { response = "Accept", comment = "On it" }));
        await Ok<TaskDetail>(raj.PostAsJsonAsync($"/api/tasks/{id}/commitments", new { commitmentDate = "2026-10-08", text = "Will finish by Thursday" }));
        var ext = await Ok<TaskDetail>(raj.PostAsJsonAsync($"/api/tasks/{id}/extensions", new { proposedDueDate = "2026-10-12", reason = "Portal outage" }));
        var extension = Assert.Single(ext.Extensions);
        await Ok<TaskDetail>(ceo.PostAsJsonAsync($"/api/extensions/{extension.Id}/decision", new { approve = true, note = "OK" }));
        await Ok<TaskDetail>(raj.PostAsJsonAsync($"/api/tasks/{id}/snooze", new { reason = "Vendor", resumeOn = "2026-10-09", note = "Waiting for the bank" }));
        await Ok<TaskDetail>(raj.PostAsync($"/api/tasks/{id}/resume-followup", null));
        var withDep = await Ok<TaskDetail>(ceo.GetAsync($"/api/tasks/{id}"));
        var dep = withDep.Dependencies.Single();
        await Ok<TaskDetail>(raj.PostAsync($"/api/tasks/{id}/dependencies/{dep.Id}/resolve", null));
        var added = await Ok<TaskDetail>(ceo.PostAsJsonAsync($"/api/tasks/{id}/dependencies", new { taskId = normal.Summary.Id, type = "FinishToStart" }));
        await Ok<TaskDetail>(ceo.DeleteAsync($"/api/tasks/{id}/dependencies/{added.Dependencies.Single(d => d.TaskId == normal.Summary.Id).Id}"));
        Assert.True((await Ok<QueuedResult>(ceo.PostAsJsonAsync($"/api/tasks/{id}/remind", new { email = true, teams = true, message = "Status please" }))).Queued > 0);
        await Ok<TaskDetail>(ceo.PostAsJsonAsync($"/api/tasks/{id}/escalate", new { targets = "DepartmentHead, Ceo", note = "Auditors are waiting" }));
        await Ok<TaskDetail>(ceo.PostAsJsonAsync($"/api/tasks/{normal.Summary.Id}/reassign", new { newPrimaryAssigneeId = Org.FinanceMember, note = "Meera is on leave" }));
        await Ok<List<AuditEntryDto>>(ceo.GetAsync($"/api/tasks/{id}/audit"));
        await Ok<List<WorkloadDto>>(ceo.GetAsync($"/api/workload?employeeIds={Org.FinanceMember},{Org.FinanceMember2}"));
        await Ok<List<WorkloadDto>>(ceo.GetAsync($"/api/workload?teamId={Org.FinanceTeam}"));
        await Ok<ResolvedDraft>(ceo.PostAsJsonAsync("/api/tasks/parse", new { text = "Ask Raj to file GST by Friday", voice = true }));
        await Ok<PagedResult<TaskListItem>>(ceo.PostAsJsonAsync("/api/tasks/query", new
        {
            text = "gst", statuses = new[] { "Accepted", "InProgress" }, priorities = new[] { "High" }, criticalities = new[] { "Critical" }, health = new[] { "Critical", "AtRisk" },
            tag = "gst", sort = "priority", waitingForCeo = false, page = 1, pageSize = 10,
        }));
        await Ok<PagedResult<TaskListItem>>(ceo.PostAsJsonAsync("/api/tasks/query", new { scope = "team", bucket = "SeverelyOverdue", waitingForCeo = true, sort = "updated" }));
        await Ok<PagedResult<TaskListItem>>(raj.PostAsJsonAsync("/api/tasks/query", new { scope = "mine", noResponse = true, recurring = false, topLevelOnly = true }));

        // Executive views
        var wb = await Ok<CeoWorkbench>(ceo.GetAsync("/api/workbench"));
        Assert.Equal("3 tasks are active.", wb.Summary.Points[0].Text);
        await Ok<MyDashboard>(raj.GetAsync("/api/dashboard/me"));
        var teams = await Ok<List<NamedRef>>(ceo.GetAsync("/api/dashboard/teams"));
        Assert.Equal(2, teams.Count);
        await Ok<TeamDashboard>(ceo.GetAsync($"/api/dashboard/team/{Org.FinanceTeam}"));
        var attention = await Ok<List<AttentionItemDto>>(ceo.GetAsync("/api/attention?status=All"));
        var item = attention.First(a => a.Status == AttentionStatus.Open);
        Assert.Equal(AttentionStatus.Snoozed, (await Ok<AttentionItemDto>(ceo.PostAsJsonAsync($"/api/attention/{item.Id}/decision", new { action = "snooze", note = "Tomorrow" }))).Status);
        Assert.Equal(AttentionStatus.Open, (await Ok<AttentionItemDto>(ceo.PostAsJsonAsync($"/api/attention/{item.Id}/decision", new { action = "reopen" }))).Status);
        Assert.Equal(AttentionStatus.Resolved, (await Ok<AttentionItemDto>(ceo.PostAsJsonAsync($"/api/attention/{item.Id}/decision", new { action = "resolve", note = "Handled" }))).Status);
        await Ok<ExecutiveSummary>(ceo.GetAsync("/api/summary"));
        await Ok<DigestDto>(ceo.GetAsync("/api/digest/preview"));
        await Ok<QueuedResult>(ceo.PostAsync("/api/digest/send", null));
        await Ok<WeeklyReviewDto>(ceo.GetAsync("/api/review/weekly?weekStart=2026-09-28"));
        await Ok<QueuedResult>(ceo.PostAsync("/api/review/weekly/send", null));
        Assert.Equal(HttpStatusCode.Forbidden, (await raj.GetAsync("/api/digest/preview")).StatusCode);
        var ask = await Ok<AskAnswer>(ceo.PostAsJsonAsync("/api/ask", new { question = "gst" }));
        Assert.NotEmpty(ask.Tasks);
        await Ok<List<CommitmentDto>>(ceo.GetAsync("/api/commitments?status=Open&toMe=true&from=2026-10-01&to=2026-10-31"));
        var report = await Ok<ReportDto>(ceo.GetAsync("/api/reports?from=2026-09-01&to=2026-10-07"));
        Assert.Contains(report.Departments, d => d.Name == "Finance");
        await Ok<ReportDto>(ceo.GetAsync($"/api/reports?teamId={Org.FinanceTeam}&departmentId={Org.Finance}"));
        var inbox = await Ok<List<InboxItem>>(raj.GetAsync("/api/inbox"));
        Assert.NotEmpty(inbox);
        await Ok(raj.PostAsync($"/api/inbox/{inbox[0].Id}/read", null));
        await Ok(raj.PostAsync("/api/inbox/read-all", null));
        Assert.Equal(0, (await Ok<CountResult>(raj.GetAsync("/api/inbox/unread-count"))).Count);

        // Search, lookups, views, system
        var search = await Ok<SearchResult>(ceo.GetAsync("/api/search?q=gst"));
        Assert.Contains(search.Hits, h => h.Kind == "task");
        Assert.Contains(search.Hits, h => h.Kind == "tag");
        await Ok<SearchResult>(ceo.GetAsync("/api/search?q=finance"));
        var lookups = await Ok<Lookups>(ceo.GetAsync("/api/lookups"));
        Assert.Contains(lookups.People, p => p.Id == Org.Ceo && p.IsExecutive);
        var view = await Ok<SavedViewDto>(ceo.PostAsJsonAsync("/api/views", new { name = "Overdue finance", filter = new { departmentId = Org.Finance, overdue = true }, isShared = true }));
        await Ok<SavedViewDto>(ceo.PutAsJsonAsync($"/api/views/{view.Id}", new { name = "Overdue finance work", filter = new { departmentId = Org.Finance }, isShared = false }));
        Assert.Single(await Ok<List<SavedViewDto>>(ceo.GetAsync("/api/views")));
        await Ok(ceo.DeleteAsync($"/api/views/{view.Id}"));
        await Ok<SystemInfoDto>(ceo.GetAsync("/api/system/info"));

        // Messages (no integration on this host — empty but working)
        await Ok<PagedResult<MessageDto>>(ceo.GetAsync("/api/messages?text=gst&channel=Email&status=Processed&unlinked=true&from=2026-10-01&to=2026-10-07&page=1"));
        await Ok<List<MessageDto>>(ceo.GetAsync("/api/messages/review"));
        Assert.Equal(HttpStatusCode.NotFound, (await ceo.GetAsync($"/api/messages/{Guid.NewGuid()}")).StatusCode);

        // Suggestion routes 404 for unknown ids
        Assert.Equal(HttpStatusCode.NotFound, (await ceo.PostAsync($"/api/suggestions/{Guid.NewGuid()}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task Planning_recurring_and_admin_routes_respond()
    {
        await using var factory = new ApiFactory();
        var ceo = await factory.SignInAsync("ceo");
        var admin = await factory.SignInAsync("admin");

        // Projects, meetings, templates
        var project = await Ok<ProjectSummary>(ceo.PostAsJsonAsync("/api/projects", new { code = "cp", name = "Customer Portal (CP)", ownerId = Org.DevLead, departmentId = Org.Development }));
        Assert.Equal("CP", project.Code);
        await Ok<ProjectSummary>(ceo.PutAsJsonAsync($"/api/projects/{project.Id}", new { code = "CP", name = "Customer Portal (CP)", targetDate = "2026-12-31", visibility = "Normal", isActive = true }));
        await Ok<TaskDetail>(ceo.PostAsJsonAsync("/api/tasks", new { title = "Portal login", primaryAssigneeId = Org.DevMember, projectId = project.Id, dueDate = "2026-10-02" }));
        Assert.Single(await Ok<List<ProjectSummary>>(ceo.GetAsync("/api/projects")));
        var projectDetail = await Ok<ProjectDetail>(ceo.GetAsync($"/api/projects/{project.Id}"));
        Assert.Equal("Off Track", projectDetail.Summary.Status);

        var meeting = await Ok<MeetingDto>(ceo.PostAsJsonAsync("/api/meetings", new
        {
            title = "Portal steering", heldAt = "2026-10-06T09:00:00Z", organizerId = Org.Ceo, participantIds = new[] { Org.DevLead },
            projectId = project.Id, actionItems = new[] { new { actionText = "Share the release plan", ownerId = Org.DevLead, dueDate = "2026-10-09", criticality = "Important" } },
        }));
        await Ok<MeetingDto>(ceo.PutAsJsonAsync($"/api/meetings/{meeting.Id}", new
        {
            title = "Portal steering committee", heldAt = "2026-10-06T09:00:00Z", organizerId = Org.Ceo, participantIds = new[] { Org.DevLead, Org.DevMember },
            actionItems = new[] { new { id = meeting.ActionItems[0].Id, actionText = "Share the release plan", ownerId = Org.DevLead, dueDate = "2026-10-09", criticality = "Important" } },
        }));
        var withTasks = await Ok<MeetingDto>(ceo.PostAsJsonAsync($"/api/meetings/{meeting.Id}/create-tasks", new { }));
        Assert.NotNull(withTasks.ActionItems[0].TaskNumber);
        Assert.Single(await Ok<List<MeetingDto>>(ceo.GetAsync("/api/meetings")));
        await Ok<MeetingDto>(ceo.GetAsync($"/api/meetings/{meeting.Id}"));

        var template = await Ok<TemplateDto>(ceo.PostAsJsonAsync("/api/templates", new
        {
            name = "New hire onboarding", category = "HR", priority = "Normal", criticality = "Normal", requiresApproval = false, roles = new[] { "Owner" },
            items = new[] { new { key = "laptop", title = "Issue laptop", assigneeRole = "Owner", dueOffsetWorkingDays = 1 } }, approvalFlow = Array.Empty<string>(),
        }));
        await Ok<TemplateDto>(ceo.PutAsJsonAsync($"/api/templates/{template.Id}", new
        {
            name = "New hire onboarding", category = "HR", priority = "High", criticality = "Important", requiresApproval = true, roles = new[] { "Owner" },
            items = new[] { new { key = "laptop", title = "Issue laptop", assigneeRole = "Owner", dueOffsetWorkingDays = 2 } }, approvalFlow = new[] { "Delegator" }, isActive = true,
        }));
        Assert.Single(await Ok<List<TemplateDto>>(ceo.GetAsync("/api/templates")));
        await Ok<TemplateDto>(ceo.GetAsync($"/api/templates/{template.Id}"));
        var instantiated = await Ok<TaskDetail>(ceo.PostAsJsonAsync($"/api/templates/{template.Id}/instantiate", new
        {
            title = "Onboard Priya", anchorDate = "2026-10-08", roleAssignments = new Dictionary<string, Guid> { ["Owner"] = Org.DevLead },
        }));
        Assert.Single(instantiated.Subtasks);

        // Recurring
        var preview = await Ok<List<OccurrencePreview>>(ceo.PostAsJsonAsync("/api/recurring/preview", new
        {
            rule = new { frequency = "Monthly", dayOfMonth = 20 }, due = new { kind = "AfterWorkingDays", value = 2 }, holidayHandling = "NextWorkingDay",
            titleTemplate = "GST return {Month}", count = 3,
        }));
        Assert.Equal(3, preview.Count);
        var rec = await Ok<RecurringDefinitionDto>(ceo.PostAsJsonAsync("/api/recurring", new
        {
            titleTemplate = "GST return {Month}", primaryAssigneeId = Org.FinanceMember, departmentId = Org.Finance,
            rule = new { frequency = "Monthly", dayOfMonth = 20 }, due = new { kind = "SameDay" },
        }));
        await Ok<RecurringDefinitionDto>(ceo.GetAsync($"/api/recurring/{rec.Id}"));
        Assert.Single(await Ok<List<RecurringDefinitionDto>>(ceo.GetAsync("/api/recurring")));
        await Ok<RecurringDefinitionDto>(ceo.PostAsJsonAsync($"/api/recurring/{rec.Id}/pause", new { reason = "Audit month" }));
        await Ok<RecurringDefinitionDto>(ceo.PostAsync($"/api/recurring/{rec.Id}/resume", null));
        var skipped = await Ok<RecurringInstanceDto>(ceo.PostAsJsonAsync($"/api/recurring/{rec.Id}/skip", new { nominalDate = "2026-11-20", reason = "Holiday season" }));
        Assert.Equal(OccurrenceOutcome.SkippedByUser, skipped.Outcome);
        await Ok<List<RecurringInstanceDto>>(ceo.GetAsync($"/api/recurring/instances?definitionId={rec.Id}&outcome=SkippedByUser,Created&from=2026-10-01&to=2026-12-31&missedOnly=false"));
        await Ok<RecurringDefinitionDto>(ceo.PutAsJsonAsync($"/api/recurring/{rec.Id}", new
        {
            definition = new { titleTemplate = "GST return {Month}", primaryAssigneeId = Org.FinanceMember, rule = new { frequency = "Monthly", dayOfMonth = 18 }, due = new { kind = "SameDay" } },
            scope = "EntireSeries",
        }));
        await Ok<RecurringDefinitionDto>(ceo.PostAsJsonAsync($"/api/recurring/{rec.Id}/end", new { reason = "Moved to the auditors" }));

        // Administration
        var settings = await Ok<RuntimeSettings>(admin.GetAsync("/api/admin/settings"));
        var updated = await Ok<RuntimeSettings>(admin.PutAsJsonAsync("/api/admin/settings", settings with { SeverelyOverdueDays = 10, DailyDigestTime = new TimeOnly(9, 0) }, ApiFactory.Json));
        Assert.Equal(10, updated.SeverelyOverdueDays);
        Assert.Equal(10, (await Ok<RuntimeSettings>(admin.GetAsync("/api/admin/settings"))).SeverelyOverdueDays);
        var bad = await admin.PutAsJsonAsync("/api/admin/settings", settings with { SeverelyOverdueDays = 0 }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var dept = await Ok<DepartmentDto>(admin.PostAsJsonAsync("/api/admin/departments", new { code = "ops", name = "Operations" }));
        await Ok<DepartmentDto>(admin.PutAsJsonAsync($"/api/admin/departments/{dept.Id}", new { code = "OPS", name = "Operations", headId = Org.DevLead, parentDepartmentId = (Guid?)null, isActive = true }));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/departments", new { code = "FIN", name = "Finance 2" })).StatusCode);
        var emp = await Ok<EmployeeDto>(admin.PostAsJsonAsync("/api/admin/employees", new
        {
            employeeCode = "OPS1", displayName = "Olga Ops", email = "olga@wings.example", jobTitle = "Ops Lead", departmentId = dept.Id, isActive = true, preferredChannels = "Email, InApp",
        }));
        await Ok<EmployeeDto>(admin.PutAsJsonAsync($"/api/admin/employees/{emp.Id}", new { employeeCode = "OPS1", displayName = "Olga Ops", email = "olga@wings.example", departmentId = dept.Id, isActive = true }));
        var withLogin = await Ok<EmployeeDto>(admin.PutAsJsonAsync($"/api/admin/employees/{emp.Id}/account", new { userName = "olga", roles = "TeamLeader", enabled = true, newPassword = "Sup3rSecret!" }));
        Assert.True(withLogin.HasLogin);
        await factory.SignInAsync("olga", "Sup3rSecret!");                   // the new login works
        var team = await Ok<TeamDto>(admin.PostAsJsonAsync("/api/admin/teams", new { name = "Ops Team", departmentId = dept.Id, leaderId = emp.Id, isActive = true, memberIds = new[] { Org.DevMember } }));
        Assert.Equal(2, team.Members.Count);
        await Ok<TeamDto>(admin.PutAsJsonAsync($"/api/admin/teams/{team.Id}", new { name = "Ops Team", departmentId = dept.Id, leaderId = emp.Id, isActive = true, memberIds = Array.Empty<Guid>() }));
        Assert.Single((await Ok<TeamDto>(admin.GetAsync($"/api/admin/teams/{team.Id}"))).Members);
        await Ok<List<TeamDto>>(admin.GetAsync("/api/admin/teams"));
        await Ok<List<EmployeeDto>>(admin.GetAsync("/api/admin/employees"));
        await Ok<List<DepartmentDto>>(admin.GetAsync("/api/admin/departments"));

        var calendar = (await Ok<List<CalendarDto>>(admin.GetAsync("/api/admin/calendars"))).Single();
        var withHoliday = await Ok<CalendarDto>(admin.PostAsJsonAsync($"/api/admin/calendars/{calendar.Id}/holidays", new { date = "2026-11-08", name = "Diwali", kind = "Public" }));
        await Ok<CalendarDto>(admin.DeleteAsync($"/api/admin/calendars/{calendar.Id}/holidays/{withHoliday.Holidays.Single().Id}"));
        await Ok<CalendarDto>(admin.PostAsJsonAsync("/api/admin/calendars", new { name = "UAE", timeZoneId = "Asia/Dubai", weekendDays = "Saturday,Sunday", workdayStart = "09:00:00", workdayEnd = "18:00:00", isDefault = false }));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/calendars", new { name = "Bad", timeZoneId = "Mars/Olympus", weekendDays = "Sunday", workdayStart = "09:00:00", workdayEnd = "18:00:00" })).StatusCode);

        var policy = await Ok<EscalationPolicyDto>(admin.PostAsJsonAsync("/api/admin/escalation-policies", new
        {
            name = "Critical ladder", appliesTo = "Critical,BusinessCritical", isDefault = false, isActive = true,
            steps = new[]
            {
                new { order = 1, trigger = "Overdue", threshold = 0.0, unit = "Hours", targets = "Assignee, TeamLeader", channels = "Email, Teams", level = 2, raiseCeoAttention = false },
                new { order = 2, trigger = "Overdue", threshold = 1.0, unit = "Days", targets = "Ceo", channels = "Email", level = 5, raiseCeoAttention = true },
            },
        }));
        Assert.Equal(2, policy.Steps.Count);
        await Ok<EscalationPolicyDto>(admin.PutAsJsonAsync($"/api/admin/escalation-policies/{policy.Id}", new
        {
            name = "Critical ladder", appliesTo = "Critical", isDefault = true, isActive = true,
            steps = new[] { new { order = 1, trigger = "NoResponse", threshold = 2.0, unit = "WorkingDays", targets = "Delegator", channels = "Email", level = 3, raiseCeoAttention = false } },
        }));
        Assert.Single((await Ok<EscalationPolicyDto>(admin.GetAsync($"/api/admin/escalation-policies/{policy.Id}"))).Steps);
        await Ok<List<EscalationPolicyDto>>(admin.GetAsync("/api/admin/escalation-policies"));

        var monitor = await Ok<MonitorOverview>(admin.GetAsync("/api/admin/monitor"));
        Assert.Equal("Disabled", monitor.Mode);
        var jobs = await Ok<List<JobDto>>(admin.GetAsync("/api/admin/jobs"));
        Assert.Empty(jobs);                                                  // jobs are registered by the engine, which is off in tests
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/jobs/digest.daily/run", null)).StatusCode);
        await Ok<List<NotificationRow>>(admin.GetAsync("/api/admin/notifications?status=Pending"));
        var failedIds = (await Ok<List<NotificationRow>>(admin.GetAsync("/api/admin/notifications"))).Select(n => n.Id).Take(2).ToList();
        await Ok<RetryResult>(admin.PostAsJsonAsync("/api/admin/notifications/retry", new { ids = failedIds }));
        await Ok<List<IntegrationErrorDto>>(admin.GetAsync("/api/admin/errors"));
        await Ok<List<MessageDto>>(admin.GetAsync("/api/admin/messages/failed"));
        await Ok(admin.PostAsync("/api/admin/sync/run-now", null));
        var auditRows = await Ok<List<AuditEntryDto>>(admin.GetAsync("/api/admin/audit?entityType=WorkTask&from=2026-10-01&to=2026-10-31&page=1"));
        Assert.NotEmpty(auditRows);
        var verify = await Ok<AuditVerifyResult>(admin.GetAsync($"/api/admin/audit/verify/{auditRows.First(a => a.TaskId != null).TaskId}"));
        Assert.True(verify.Ok);
        var raj = await factory.SignInAsync("raj");
        // 404 until the emulator console is mapped after the integration merge; 403 afterwards — never 200 for a team member.
        Assert.Contains((await raj.GetAsync("/api/m365-sim/users")).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
    }
}
