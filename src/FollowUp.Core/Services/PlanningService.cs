using System.Text.Json;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Services;

/// <summary>
/// Planning: projects (spec §46), meetings with decisions and action items that become tasks (§47), reusable task
/// templates with role placeholders, working-day due offsets, dependencies and approval flows (§51), and saved
/// task-list views (§53).
/// </summary>
public sealed class PlanningService(
    FollowUpDbContext db,
    ActorContext actor,
    IClock clock,
    CalendarProvider calendars,
    TaskService tasks,
    AuditService audit,
    DashboardService dashboards,
    IOptions<FollowUpOptions> options)
{
    private readonly FollowUpOptions _o = options.Value;

    private bool IsManager => actor.IsSystem || actor.Viewer.IsManagement || actor.Viewer.IsAdmin;

    // ═══════════════════════════════ Projects ═══════════════════════════════

    public async Task<ProjectSummary> SaveProjectAsync(Guid? id, ProjectInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Code)) throw new ValidationException("code_required", "Project code is required.");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ValidationException("name_required", "Project name is required.");
        var project = id is { } x ? await db.Projects.FirstOrDefaultAsync(p => p.Id == x, ct) ?? throw new NotFoundException("Project") : new Project();
        if (id is null && !IsManager) throw new ForbiddenException("Only managers can create projects.");
        if (id is not null && !actor.IsSystem && !actor.Viewer.IsExecutive && !actor.Viewer.IsAdmin && project.OwnerId != actor.EmployeeId
            && !(project.DepartmentId is { } d && actor.Viewer.ManagedDepartmentIds.Contains(d)))
            throw new ForbiddenException("Only the project owner, the department head or an executive can change this project.");
        var code = input.Code.Trim().ToUpperInvariant();
        if (await db.Projects.AnyAsync(p => p.Code == code && p.Id != project.Id, ct)) throw new ConflictException("duplicate_code", $"Project code {code} is already used.");
        if (input.OwnerId is { } owner && !await db.Employees.AnyAsync(e => e.Id == owner, ct)) throw new ValidationException("owner_invalid", "Project owner not found.");
        if (input.DepartmentId is { } dep && !await db.Departments.AnyAsync(e => e.Id == dep, ct)) throw new ValidationException("department_invalid", "Department not found.");
        if (input.StartDate is { } s && input.TargetDate is { } t && t < s) throw new ValidationException("dates", "The target date is before the start date.");
        project.Code = code;
        project.Name = input.Name.Trim();
        project.Description = input.Description;
        project.OwnerId = input.OwnerId ?? (id is null ? actor.EmployeeId : project.OwnerId);
        project.DepartmentId = input.DepartmentId;
        project.StartDate = input.StartDate;
        project.TargetDate = input.TargetDate;
        project.Visibility = input.Visibility;
        project.IsActive = input.IsActive;
        if (id is null) db.Projects.Add(project);
        audit.Record(id is null ? "ProjectCreated" : "ProjectUpdated", nameof(Project), project.Id, details: $"{code} {project.Name}");
        await db.SaveChangesAsync(ct);
        return await dashboards.ProjectSummaryAsync(project.Id, ct);
    }

    // ═══════════════════════════════ Meetings (spec §47) ═══════════════════════════════

    private bool CanSee(Meeting m)
    {
        if (actor.IsSystem || actor.Viewer.IsExecutive) return true;
        var me = actor.EmployeeId;
        return m.OrganizerId == me || (me is { } x && (m.ParticipantIds.Contains(x) || m.ActionItems.Any(a => a.OwnerId == x)));
    }

    private bool CanEdit(Meeting m) => actor.IsSystem || actor.Viewer.IsExecutive || actor.Viewer.IsAdmin || m.OrganizerId == actor.EmployeeId;

    public async Task<List<MeetingDto>> MeetingsAsync(CancellationToken ct)
    {
        var rows = await db.Meetings.AsNoTracking().Include(m => m.ActionItems).OrderByDescending(m => m.HeldAt).Take(300).ToListAsync(ct);
        return await ToDtosAsync(rows.Where(CanSee).ToList(), ct);
    }

    public async Task<MeetingDto> MeetingAsync(Guid id, CancellationToken ct)
    {
        var m = await db.Meetings.AsNoTracking().Include(x => x.ActionItems).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null || !CanSee(m)) throw new NotFoundException("Meeting");
        return (await ToDtosAsync([m], ct))[0];
    }

    public async Task<MeetingDto> SaveMeetingAsync(Guid? id, MeetingInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Title)) throw new ValidationException("title_required", "Give the meeting a title.");
        var meeting = id is { } x ? await db.Meetings.Include(m => m.ActionItems).FirstOrDefaultAsync(m => m.Id == x, ct) : new Meeting();
        if (meeting is null || (id is not null && !CanSee(meeting))) throw new NotFoundException("Meeting");
        if (id is not null && !CanEdit(meeting)) throw new ForbiddenException("Only the organiser can change this meeting.");
        var peopleIds = (input.ParticipantIds ?? []).Concat((input.ActionItems ?? []).Where(a => a.OwnerId is not null).Select(a => a.OwnerId!.Value))
            .Concat(new[] { input.OrganizerId }.Where(x => x is not null).Select(x => x!.Value)).Distinct().ToList();
        if (await db.Employees.CountAsync(e => peopleIds.Contains(e.Id), ct) != peopleIds.Count) throw new ValidationException("person_invalid", "One or more people were not found.");
        if (input.ProjectId is { } p && !await db.Projects.AnyAsync(x => x.Id == p, ct)) throw new ValidationException("project_invalid", "Project not found.");

        meeting.Title = input.Title.Trim();
        meeting.HeldAt = DateTime.SpecifyKind(input.HeldAt.Kind == DateTimeKind.Local ? input.HeldAt.ToUniversalTime() : input.HeldAt, DateTimeKind.Utc);
        meeting.OrganizerId = input.OrganizerId ?? meeting.OrganizerId ?? actor.EmployeeId;
        meeting.Location = input.Location;
        meeting.Notes = input.Notes;
        meeting.ProjectId = input.ProjectId;
        meeting.ParticipantIds = (input.ParticipantIds ?? []).Distinct().ToList();

        var keep = new HashSet<Guid>();
        foreach (var a in input.ActionItems ?? [])
        {
            if (string.IsNullOrWhiteSpace(a.ActionText)) throw new ValidationException("action_required", "Each action item needs a description.");
            var item = a.Id is { } aid ? meeting.ActionItems.FirstOrDefault(i => i.Id == aid) : null;
            if (item is null)
            {
                item = new MeetingActionItem { MeetingId = meeting.Id };
                if (id is null) meeting.ActionItems.Add(item); else db.MeetingActionItems.Add(item);
            }
            keep.Add(item.Id);
            if (item.TaskId is not null && item.Status == MeetingActionStatus.TaskCreated) continue;   // already delegated — the task is the source of truth
            item.Decision = a.Decision;
            item.ActionText = a.ActionText.Trim();
            item.OwnerId = a.OwnerId;
            item.DueDate = a.DueDate;
            item.Criticality = a.Criticality;
        }
        foreach (var gone in meeting.ActionItems.Where(i => !keep.Contains(i.Id) && i.TaskId is null).ToList())
            gone.Status = MeetingActionStatus.Cancelled;
        if (id is null) db.Meetings.Add(meeting);
        audit.Record(id is null ? "MeetingCreated" : "MeetingUpdated", nameof(Meeting), meeting.Id, details: $"{meeting.Title} ({keep.Count} action item(s))");
        await db.SaveChangesAsync(ct);
        return await MeetingAsync(meeting.Id, ct);
    }

    /// <summary>Turns open action items into tasks: owner = primary assignee, follow-up every 2 days unless specified.</summary>
    public async Task<MeetingDto> CreateTasksFromActionItemsAsync(Guid meetingId, CreateActionTasksRequest request, CancellationToken ct)
    {
        var meeting = await db.Meetings.Include(m => m.ActionItems).FirstOrDefaultAsync(m => m.Id == meetingId, ct);
        if (meeting is null || !CanSee(meeting)) throw new NotFoundException("Meeting");
        if (!CanEdit(meeting) && !IsManager) throw new ForbiddenException("Only the organiser or a manager can create tasks from this meeting.");
        var wanted = request.ActionItemIds is { Count: > 0 } ids ? ids.ToHashSet() : null;
        var items = meeting.ActionItems.Where(i => i.Status == MeetingActionStatus.Open && i.TaskId is null && (wanted is null || wanted.Contains(i.Id))).ToList();
        if (wanted is not null && items.Any(i => i.OwnerId is null))
            throw new ValidationException("owner_required", "Every selected action item needs an owner before it can become a task.");
        var followUp = request.FollowUp ?? FollowUpSchedule.EveryDays(2, _o.DefaultFollowUpTime);
        foreach (var item in items.Where(i => i.OwnerId is not null))
        {
            var description = $"Action item from the meeting \"{meeting.Title}\" held on {meeting.HeldAt:d MMM yyyy}."
                + (string.IsNullOrWhiteSpace(item.Decision) ? "" : $"\nDecision: {item.Decision}");
            await tasks.CreateAsync(new CreateTaskRequest
            {
                Title = NotificationService.Shorten(item.ActionText, 400),
                Description = description,
                PrimaryAssigneeId = item.OwnerId,
                DueDate = item.DueDate,
                Criticality = item.Criticality,
                Priority = item.Criticality >= Criticality.Critical ? TaskPriority.High : TaskPriority.Normal,
                ProjectId = meeting.ProjectId,
                Source = TaskSource.Meeting,
                MeetingActionItemId = item.Id,
                FollowUp = followUp,
            }, ct, meeting.OrganizerId);
        }
        return await MeetingAsync(meetingId, ct);
    }

    private async Task<List<MeetingDto>> ToDtosAsync(List<Meeting> meetings, CancellationToken ct)
    {
        var personIds = meetings.SelectMany(m => m.ParticipantIds.Concat(m.ActionItems.Where(a => a.OwnerId is not null).Select(a => a.OwnerId!.Value))
            .Concat(new[] { m.OrganizerId }.Where(x => x is not null).Select(x => x!.Value))).Distinct().ToList();
        var people = await db.Employees.AsNoTracking().Where(e => personIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new PersonRef(e.Id, e.DisplayName, e.JobTitle, e.Email), ct);
        var taskIds = meetings.SelectMany(m => m.ActionItems.Where(a => a.TaskId is not null).Select(a => a.TaskId!.Value)).Distinct().ToList();
        var taskInfo = await db.Tasks.AsNoTracking().Where(t => taskIds.Contains(t.Id)).Select(t => new { t.Id, t.Number, t.Status }).ToDictionaryAsync(t => t.Id, ct);
        var projectIds = meetings.Where(m => m.ProjectId is not null).Select(m => m.ProjectId!.Value).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        PersonRef? P(Guid? id) => id is { } x && people.TryGetValue(x, out var p) ? p : null;
        return meetings.Select(m => new MeetingDto(m.Id, m.Title, m.HeldAt, P(m.OrganizerId), m.Location, m.Notes,
            m.ProjectId is { } pid && projects.TryGetValue(pid, out var pn) ? new NamedRef(pid, pn) : null,
            m.ParticipantIds.Select(x => P(x)).Where(x => x is not null).Select(x => x!).ToList(),
            m.ActionItems.OrderBy(a => a.Status == MeetingActionStatus.Cancelled).ThenBy(a => a.DueDate).Select(a => new ActionItemDto(a.Id, a.Decision, a.ActionText,
                P(a.OwnerId), a.DueDate, a.Criticality, a.TaskId,
                a.TaskId is { } t && taskInfo.TryGetValue(t, out var ti) ? ti.Number : null,
                a.TaskId is { } t2 && taskInfo.TryGetValue(t2, out var ti2) ? ti2.Status : null, a.Status)).ToList())).ToList();
    }

    // ═══════════════════════════════ Templates (spec §51) ═══════════════════════════════

    public async Task<List<TemplateDto>> TemplatesAsync(CancellationToken ct, Guid? id = null)
    {
        var q = db.TaskTemplates.AsNoTracking();
        if (id is { } x) q = q.Where(t => t.Id == x);
        var rows = await q.OrderByDescending(t => t.IsActive).ThenBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<TemplateDto> TemplateAsync(Guid id, CancellationToken ct) => (await TemplatesAsync(ct, id)).FirstOrDefault() ?? throw new NotFoundException("Template");

    private static TemplateDto ToDto(TaskTemplate t) => new(t.Id, t.Name, t.Category, t.Description, t.Priority, t.Criticality, t.RequiresApproval,
        t.EscalationPolicyId, t.FollowUp, t.Roles, t.Items, t.ApprovalFlow, t.IsActive);

    public async Task<TemplateDto> SaveTemplateAsync(Guid? id, TemplateInput input, CancellationToken ct)
    {
        if (!IsManager) throw new ForbiddenException("Only managers can maintain templates.");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ValidationException("name_required", "Give the template a name.");
        var items = input.Items ?? [];
        ValidateItems(items);
        var roles = (input.Roles ?? []).Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var role in items.Where(i => !string.IsNullOrWhiteSpace(i.AssigneeRole)).Select(i => i.AssigneeRole!.Trim()))
            if (!roles.Contains(role, StringComparer.OrdinalIgnoreCase)) roles.Add(role);
        var t = id is { } x ? await db.TaskTemplates.FirstOrDefaultAsync(tt => tt.Id == x, ct) ?? throw new NotFoundException("Template") : new TaskTemplate();
        var name = input.Name.Trim();
        if (await db.TaskTemplates.AnyAsync(tt => tt.Name == name && tt.Id != t.Id, ct)) throw new ConflictException("duplicate_name", $"A template named {name} already exists.");
        if (input.EscalationPolicyId is { } pol && !await db.EscalationPolicies.AnyAsync(p => p.Id == pol, ct)) throw new ValidationException("policy_invalid", "Escalation policy not found.");
        t.Name = name;
        t.Category = string.IsNullOrWhiteSpace(input.Category) ? "General" : input.Category.Trim();
        t.Description = input.Description;
        t.Priority = input.Priority;
        t.Criticality = input.Criticality;
        t.RequiresApproval = input.RequiresApproval;
        t.EscalationPolicyId = input.EscalationPolicyId;
        t.FollowUp = input.FollowUp ?? FollowUpSchedule.NoneSchedule;
        t.Roles = roles;
        t.Items = items;
        t.ApprovalFlow = input.ApprovalFlow ?? [];
        t.IsActive = input.IsActive;
        if (id is null) db.TaskTemplates.Add(t);
        audit.Record(id is null ? "TemplateCreated" : "TemplateUpdated", nameof(TaskTemplate), t.Id, details: $"{name}: {items.Count} item(s)");
        await db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    private static void ValidateItems(List<TemplateItem> items)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
        {
            if (string.IsNullOrWhiteSpace(i.Key)) throw new ValidationException("item_key_required", "Every template item needs a key.");
            if (string.IsNullOrWhiteSpace(i.Title)) throw new ValidationException("item_title_required", $"Template item {i.Key} needs a title.");
            if (!keys.Add(i.Key)) throw new ValidationException("item_key_duplicate", $"Template item key {i.Key} is used twice.");
            if (i.DueOffsetWorkingDays < 0) throw new ValidationException("item_offset_invalid", $"Template item {i.Key} has a negative due offset.");
        }
        var byKey = items.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
        {
            if (i.ParentKey is { } pk && !byKey.ContainsKey(pk)) throw new ValidationException("item_parent_invalid", $"Template item {i.Key} refers to an unknown parent {pk}.");
            foreach (var dep in i.DependsOn ?? [])
                if (!byKey.ContainsKey(dep) || string.Equals(dep, i.Key, StringComparison.OrdinalIgnoreCase))
                    throw new ValidationException("item_dependency_invalid", $"Template item {i.Key} depends on an unknown item {dep}.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { i.Key };
            for (var p = i.ParentKey; p is not null; p = byKey[p].ParentKey)
                if (!seen.Add(p)) throw new ValidationException("item_parent_cycle", $"Template item {i.Key} is its own ancestor.");
        }
        // Dependencies must not form a cycle.
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Visit(string key)
        {
            if (state.TryGetValue(key, out var s))
            {
                if (s == 1) throw new ValidationException("item_dependency_cycle", "Template dependencies form a cycle.");
                return;
            }
            state[key] = 1;
            foreach (var dep in byKey[key].DependsOn ?? []) Visit(dep);
            state[key] = 2;
        }
        foreach (var i in items) Visit(i.Key);
    }

    /// <summary>
    /// Creates the parent task and the template items as subtasks (ParentKey hierarchy), due on working days counted
    /// from the anchor date, assigned through the role map, linked by finish-to-start dependencies, and stamped with
    /// the template so its approval flow applies on completion.
    /// </summary>
    public async Task<WorkTask> InstantiateAsync(Guid templateId, InstantiateTemplateRequest r, CancellationToken ct)
    {
        var template = await db.TaskTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct) ?? throw new NotFoundException("Template");
        if (!template.IsActive) throw new ConflictException("template_inactive", "This template is no longer active.");
        var cal = await calendars.GetDefaultAsync(ct);
        var anchor = r.AnchorDate ?? cal.Today(clock.UtcNow);
        var roleMap = new Dictionary<string, Guid>(r.RoleAssignments ?? [], StringComparer.OrdinalIgnoreCase);
        var people = roleMap.Values.Distinct().ToList();
        if (await db.Employees.CountAsync(e => people.Contains(e.Id) && e.IsActive, ct) != people.Count)
            throw new ValidationException("role_assignee_invalid", "One or more role assignments refer to unknown or inactive people.");

        Guid? Resolve(string? role) => role is not null && roleMap.TryGetValue(role.Trim(), out var id) ? id : null;
        var parentAssignee = Resolve("Owner") ?? template.Roles.Select(Resolve).FirstOrDefault(x => x is not null)
            ?? roleMap.Values.Cast<Guid?>().FirstOrDefault() ?? actor.EmployeeId
            ?? throw new ValidationException("assignee_required", "Assign the template roles to people.");
        // Items whose role is not mapped fall back to the assignee of their parent task.
        DateOnly Due(int offset) => offset == 0 ? cal.NextWorkingDay(anchor) : cal.AddWorkingDays(anchor, offset);
        var criticality = r.Criticality ?? template.Criticality;
        var parentDue = template.Items.Count == 0 ? (DateOnly?)null : Due(template.Items.Max(i => i.DueOffsetWorkingDays));
        var parent = await tasks.CreateAsync(new CreateTaskRequest
        {
            Title = string.IsNullOrWhiteSpace(r.Title) ? template.Name : r.Title.Trim(),
            Description = template.Description,
            PrimaryAssigneeId = parentAssignee,
            ProjectId = r.ProjectId,
            TeamId = r.TeamId,
            DepartmentId = r.DepartmentId,
            Priority = template.Priority,
            Criticality = criticality,
            StartDate = anchor,
            DueDate = parentDue,
            FollowUp = template.FollowUp.IsEnabled ? template.FollowUp : null,
            RequiresApproval = template.RequiresApproval,
            EscalationPolicyId = template.EscalationPolicyId,
            Source = TaskSource.Template,
        }, ct, null, t => t.TemplateId = template.Id);

        var created = new Dictionary<string, WorkTask>(StringComparer.OrdinalIgnoreCase);
        var pending = template.Items.ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(i => i.ParentKey is null || created.ContainsKey(i.ParentKey)).ToList();
            if (ready.Count == 0) throw new ValidationException("item_parent_cycle", "The template hierarchy is invalid.");
            foreach (var item in ready)
            {
                var parentTask = item.ParentKey is { } pk ? created[pk] : parent;
                var itemCriticality = item.Criticality ?? criticality;
                var followUp = item.FollowUp ?? (template.FollowUp.IsEnabled ? template.FollowUp : null);
                created[item.Key] = await tasks.CreateAsync(new CreateTaskRequest
                {
                    Title = item.Title.Trim(),
                    Description = item.Description,
                    ParentTaskId = parentTask.Id,
                    PrimaryAssigneeId = Resolve(item.AssigneeRole) ?? parentTask.PrimaryAssigneeId ?? parentAssignee,
                    ProjectId = r.ProjectId,
                    TeamId = r.TeamId,
                    DepartmentId = r.DepartmentId,
                    Priority = template.Priority,
                    Criticality = itemCriticality,
                    StartDate = anchor,
                    DueDate = Due(item.DueOffsetWorkingDays),
                    FollowUp = followUp,
                    EscalationPolicyId = template.EscalationPolicyId,
                    Source = TaskSource.Template,
                }, ct, null, t => t.TemplateId = template.Id);
                pending.Remove(item);
            }
        }

        foreach (var item in template.Items)
            foreach (var dep in item.DependsOn ?? [])
                await tasks.AddDependencyAsync(created[item.Key].Id, new DependencyInput(created[dep].Id, DependencyType.FinishToStart), ct);

        audit.Record("TemplateInstantiated", nameof(TaskTemplate), template.Id, parent.Id, details: $"{template.Name}: {created.Count} subtask(s), anchor {anchor:yyyy-MM-dd}");
        await db.SaveChangesAsync(ct);
        return parent;
    }

    // ═══════════════════════════════ Saved views (spec §53) ═══════════════════════════════

    public async Task<List<SavedViewDto>> ViewsAsync(CancellationToken ct)
    {
        var me = actor.EmployeeId ?? Guid.Empty;
        var rows = await db.SavedViews.AsNoTracking().Where(v => v.OwnerId == me || v.IsShared).OrderByDescending(v => v.OwnerId == me).ThenBy(v => v.Name).ToListAsync(ct);
        return rows.Select(v => ToDto(v, me)).ToList();
    }

    private static SavedViewDto ToDto(SavedView v, Guid me)
    {
        TaskFilter filter;
        try { filter = JsonSerializer.Deserialize<TaskFilter>(v.FilterJson, JsonDefaults.Options) ?? new TaskFilter(); }
        catch (JsonException) { filter = new TaskFilter(); }
        return new SavedViewDto(v.Id, v.Name, filter, v.IsShared, v.OwnerId == me);
    }

    public async Task<SavedViewDto> SaveViewAsync(Guid? id, SavedViewInput input, CancellationToken ct)
    {
        var me = actor.EmployeeId ?? throw new ForbiddenException("Sign in to save views.");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ValidationException("name_required", "Give the view a name.");
        var name = input.Name.Trim();
        var view = id is { } x ? await db.SavedViews.FirstOrDefaultAsync(v => v.Id == x && (v.OwnerId == me || v.IsShared), ct) ?? throw new NotFoundException("View")
            : new SavedView { OwnerId = me, CreatedAt = clock.UtcNow };
        if (view.OwnerId != me) throw new ForbiddenException("Only the owner can change a shared view.");
        if (await db.SavedViews.AnyAsync(v => v.OwnerId == me && v.Name == name && v.Id != view.Id, ct)) throw new ConflictException("duplicate_name", $"You already have a view named {name}.");
        view.Name = name;
        view.FilterJson = JsonSerializer.Serialize(input.Filter ?? new TaskFilter(), JsonDefaults.Options);
        view.IsShared = input.IsShared;
        if (id is null) db.SavedViews.Add(view);
        await db.SaveChangesAsync(ct);
        return ToDto(view, me);
    }

    public async Task DeleteViewAsync(Guid id, CancellationToken ct)
    {
        var me = actor.EmployeeId ?? throw new ForbiddenException("Sign in to delete views.");
        var view = await db.SavedViews.FirstOrDefaultAsync(v => v.Id == id && (v.OwnerId == me || v.IsShared), ct) ?? throw new NotFoundException("View");
        if (view.OwnerId != me) throw new ForbiddenException("Only the owner can delete a shared view.");
        db.SavedViews.Remove(view);
        await db.SaveChangesAsync(ct);
    }
}
