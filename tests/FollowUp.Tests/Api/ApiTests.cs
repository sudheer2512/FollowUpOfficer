using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FollowUp.Core.Contracts;
using FollowUp.Domain;
using FollowUp.Tests.Core.Queries;

namespace FollowUp.Tests.Api;

public sealed class ApiTests
{
    private static readonly TestOrg Org = TestOrg.Default;

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            Assert.Fail($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(ApiFactory.Json))!;
    }

    [Fact]
    public async Task Login_requires_the_csrf_header_valid_credentials_and_locks_out_after_five_failures()
    {
        await using var factory = new ApiFactory();

        var health = await factory.Client(csrf: false).GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var config = await ReadAsync<AuthConfigDto>(await factory.Client(csrf: false).GetAsync("/api/auth/config"));
        Assert.False(config.EntraEnabled);
        Assert.False(config.DemoMode);

        var noCsrf = factory.Client(csrf: false);
        var blocked = await noCsrf.PostAsJsonAsync("/api/auth/login", new { userName = "ceo", password = TestOrg.Password });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("csrf_header_missing", await CodeAsync(blocked));

        var anonymous = factory.Client();
        var me401 = await anonymous.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me401.StatusCode);
        Assert.Equal("unauthenticated", await CodeAsync(me401));

        var wrong = await anonymous.PostAsJsonAsync("/api/auth/login", new { userName = "ceo", password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_credentials", await CodeAsync(wrong));

        var me = await ReadAsync<MeDto>(await anonymous.PostAsJsonAsync("/api/auth/login", new { userName = "CEO", password = TestOrg.Password }));
        Assert.True(me.IsExecutive);
        Assert.Equal(Org.Ceo, me.EmployeeId);
        Assert.Equal("Asha Rao", (await ReadAsync<MeDto>(await anonymous.GetAsync("/api/auth/me"))).Name);

        // A signed-in browser session still needs the CSRF header for changes.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/query") { Content = JsonContent.Create(new TaskFilter()) };
        var withoutHeader = await anonymous.SendAsync(Strip(request));
        Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/me")).StatusCode);

        // Lockout: the fifth wrong password locks the account; the right password is then refused too.
        var attacker = factory.Client();
        for (var i = 1; i <= 4; i++)
            Assert.Equal("invalid_credentials", await CodeAsync(await attacker.PostAsJsonAsync("/api/auth/login", new { userName = "meera", password = $"guess{i}" })));
        Assert.Equal("account_locked", await CodeAsync(await attacker.PostAsJsonAsync("/api/auth/login", new { userName = "meera", password = "guess5" })));
        Assert.Equal("account_locked", await CodeAsync(await attacker.PostAsJsonAsync("/api/auth/login", new { userName = "meera", password = TestOrg.Password })));
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.True((await attacker.PostAsJsonAsync("/api/auth/login", new { userName = "meera", password = TestOrg.Password })).IsSuccessStatusCode);
    }

    /// <summary>Removes the default CSRF header for one request by sending it through a header-less client copy.</summary>
    private static HttpRequestMessage Strip(HttpRequestMessage request)
    {
        request.Headers.Remove("X-Requested-With");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        return request;
    }

    [Fact]
    public async Task Ceo_only_task_is_404_for_a_team_member_and_admin_routes_need_the_role()
    {
        await using var factory = new ApiFactory();
        var ceo = await factory.SignInAsync("ceo");
        var created = await ReadAsync<TaskDetail>(await ceo.PostAsJsonAsync("/api/tasks", new CreateTaskRequest
        {
            Title = "Confidential: salary benchmark", PrimaryAssigneeId = Org.FinanceHead, TeamId = Org.FinanceTeam, Visibility = TaskVisibility.CeoOnly,
            DueDate = TestOrg.Today.AddDays(5),
        }, ApiFactory.Json));

        var raj = await factory.SignInAsync("raj");
        var hidden = await raj.GetAsync($"/api/tasks/{created.Summary.Id}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("not_found", await CodeAsync(hidden));
        var list = await ReadAsync<PagedResult<TaskListItem>>(await raj.PostAsJsonAsync("/api/tasks/query", new TaskFilter()));
        Assert.DoesNotContain(list.Items, t => t.Id == created.Summary.Id);

        var farah = await factory.SignInAsync("farah");
        Assert.Equal(HttpStatusCode.OK, (await farah.GetAsync($"/api/tasks/{created.Summary.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await raj.GetAsync("/api/admin/departments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ceo.GetAsync("/api/admin/departments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ceo.GetAsync("/api/admin/monitor")).StatusCode);
        var admin = await factory.SignInAsync("admin");
        var departments = await ReadAsync<List<DepartmentDto>>(await admin.GetAsync("/api/admin/departments"));
        Assert.Contains(departments, d => d.Code == "FIN" && d.Head!.Id == Org.FinanceHead);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task Create_acknowledge_progress_complete_and_approve_over_http()
    {
        await using var factory = new ApiFactory();
        var ceo = await factory.SignInAsync("ceo");
        var sam = await factory.SignInAsync("sam");

        var task = await ReadAsync<TaskDetail>(await ceo.PostAsJsonAsync("/api/tasks", new
        {
            title = "Fix the payment gateway timeout",
            primaryAssigneeId = Org.DevMember,
            criticality = "Critical",
            priority = "Urgent",
            dueDate = "2026-10-09",
            followUp = new { frequency = "Daily", timeOfDay = "10:00:00" },
        }));
        var id = task.Summary.Id;
        Assert.Equal(WorkStatus.AcknowledgementPending, task.Summary.Status);
        Assert.True(task.RequiresApproval);
        Assert.True(task.Permissions.CanManage);

        var accepted = await ReadAsync<TaskDetail>(await sam.PostAsJsonAsync($"/api/tasks/{id}/acknowledge", new { response = "Accept" }));
        Assert.Equal(WorkStatus.Accepted, accepted.Summary.Status);
        Assert.False(accepted.Permissions.CanManage);
        Assert.True(accepted.Permissions.CanComplete);

        var started = await ReadAsync<TaskDetail>(await sam.PostAsJsonAsync($"/api/tasks/{id}/status", new { status = "InProgress", reason = "Started" }));
        Assert.Equal(WorkStatus.InProgress, started.Summary.Status);
        var progressed = await ReadAsync<TaskDetail>(await sam.PostAsJsonAsync($"/api/tasks/{id}/progress", new { progressPercent = 60, note = "Retry logic in place" }));
        Assert.Equal(60, progressed.Summary.ProgressPercent);
        var commented = await ReadAsync<TaskDetail>(await sam.PostAsJsonAsync($"/api/tasks/{id}/comments", new { body = "Load test scheduled for tomorrow." }));
        Assert.Contains(commented.Timeline, t => t.Kind == "comment" && t.Body == "Load test scheduled for tomorrow.");

        var invalid = await sam.PostAsJsonAsync($"/api/tasks/{id}/status", new { status = "Completed" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("use_complete", await CodeAsync(invalid));

        var completed = await ReadAsync<TaskDetail>(await sam.PostAsJsonAsync($"/api/tasks/{id}/complete", new
        {
            note = "Deployed to production",
            evidence = new[] { new { kind = "Url", title = "Pull request", url = "https://git.example/pr/42" } },
        }));
        Assert.Equal(WorkStatus.CompletionAwaitingApproval, completed.Summary.Status);
        var approval = Assert.Single(completed.Approvals);
        Assert.Equal(Org.Ceo, approval.Approver.Id);
        Assert.False(approval.CanDecide);                                  // sam is not the approver

        var forbidden = await sam.PostAsJsonAsync($"/api/tasks/{id}/approval", new { action = "Confirm" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var ceoView = await ReadAsync<TaskDetail>(await ceo.GetAsync($"/api/tasks/{id}"));
        Assert.True(ceoView.Permissions.CanApprove);
        Assert.True(Assert.Single(ceoView.Approvals).CanDecide);
        var workbench = await ReadAsync<CeoWorkbench>(await ceo.GetAsync("/api/workbench"));
        Assert.Equal(1, workbench.Tiles.Single(t => t.Key == "awaiting-approval").Value);
        Assert.Equal(1, workbench.Tiles.Single(t => t.Key == "awaiting-ceo").Value);
        Assert.Single(workbench.Sections.Single(s => s.Key == "approvals").Approvals!);

        var done = await ReadAsync<TaskDetail>(await ceo.PostAsJsonAsync($"/api/tasks/{id}/approval", new { action = "Confirm", comment = "Well done" }));
        Assert.Equal(WorkStatus.Completed, done.Summary.Status);
        var submission = Assert.Single(done.Submissions);
        Assert.Equal("Pull request", Assert.Single(submission.Evidence).Title);
        Assert.Contains(done.Timeline, t => t.Kind == "approval" && t.Title.Contains("confirmed completion"));

        var closed = await ReadAsync<PagedResult<TaskListItem>>(await ceo.PostAsJsonAsync("/api/tasks/query", new { open = false }));
        Assert.Contains(closed.Items, t => t.Id == id);
        var audit = await ReadAsync<List<AuditEntryDto>>(await ceo.GetAsync($"/api/tasks/{id}/audit"));
        Assert.Contains(audit, a => a.Action == "Completed");
        var inbox = await ReadAsync<CountResult>(await sam.GetAsync("/api/inbox/unread-count"));
        Assert.True(inbox.Count > 0);
    }

    [Fact]
    public async Task Attachments_are_stored_downloaded_through_task_visibility_and_executables_are_refused()
    {
        await using var factory = new ApiFactory();
        var ceo = await factory.SignInAsync("ceo");
        var task = await ReadAsync<TaskDetail>(await ceo.PostAsJsonAsync("/api/tasks", new CreateTaskRequest
        {
            Title = "Collect signed contract", PrimaryAssigneeId = Org.DevMember, Visibility = TaskVisibility.CeoOnly,
        }, ApiFactory.Json));
        var sam = await factory.SignInAsync("sam");

        using var form = new MultipartFormDataContent();
        var bytes = "signed contract scan"u8.ToArray();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "contract.txt");
        form.Add(new StringContent(task.Summary.Id.ToString()), "taskId");
        var uploaded = await ReadAsync<AttachmentDto>(await sam.PostAsync("/api/attachments", form));
        Assert.Equal("contract.txt", uploaded.FileName);
        Assert.Equal(bytes.Length, uploaded.SizeBytes);

        var download = await ceo.GetAsync($"/api/attachments/{uploaded.Id}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);

        var raj = await factory.SignInAsync("raj");
        Assert.Equal(HttpStatusCode.NotFound, (await raj.GetAsync($"/api/attachments/{uploaded.Id}")).StatusCode);

        using var bad = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "setup.exe" } };
        var refused = await sam.PostAsync("/api/attachments", bad);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("file_type_blocked", await CodeAsync(refused));
    }
}
