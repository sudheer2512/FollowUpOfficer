using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Services;

/// <summary>
/// Read side for tasks (spec §52, §53, §42, §68): the filtered task list, the full task detail with its merged
/// timeline, workload before delegation, global search, audit queries and form lookups. Every task query goes
/// through <see cref="TaskAccessQueries.VisibleTo"/>; aggregation happens in memory so SQLite and SQL Server agree.
/// </summary>
public sealed partial class TaskQueryService(
    FollowUpDbContext db,
    ActorContext actor,
    IClock clock,
    CalendarProvider calendars,
    TaskProjector projector,
    TaskService tasks,
    CommitmentService commitments,
    CommunicationService communications,
    IOptions<FollowUpOptions> options)
{
    private readonly FollowUpOptions _o = options.Value;

    public Task<QueryClock> ClockAsync(CancellationToken ct) => QueryClock.CreateAsync(clock, calendars, _o.SeverelyOverdueDays, ct);

    // ═══════════════════════════════ Task list (spec §52, §53) ═══════════════════════════════

    public async Task<PagedResult<TaskListItem>> QueryAsync(TaskFilter f, CancellationToken ct)
    {
        var c = await ClockAsync(ct);
        var q = await FilterAsync(f, c, ct);
        var total = await q.CountAsync(ct);
        var page = Math.Max(1, f.Page);
        var size = Math.Clamp(f.PageSize, 1, 500);
        var items = await projector.ProjectAsync(Sort(q, f.Sort).Skip((page - 1) * size).Take(size), c.Now, c.Calendar, c.SevereDays, ct);
        return new PagedResult<TaskListItem>(items, total, page, size);
    }

    /// <summary>Projects an arbitrary (already visibility-filtered) query into list rows using the shared clock.</summary>
    public Task<List<TaskListItem>> ProjectAsync(IQueryable<WorkTask> q, QueryClock c, CancellationToken ct) =>
        projector.ProjectAsync(q, c.Now, c.Calendar, c.SevereDays, ct);

    /// <summary>The viewer's visible tasks narrowed by every <see cref="TaskFilter"/> field.</summary>
    public async Task<IQueryable<WorkTask>> FilterAsync(TaskFilter f, QueryClock c, CancellationToken ct)
    {
        var v = actor.Viewer;
        var me = actor.EmployeeId;
        var q = db.Tasks.AsNoTracking().VisibleTo(db, v);

        switch (f.Scope?.Trim().ToLowerInvariant())
        {
            case "mine" when me is { } m:
                q = q.AssignedTo(m);
                break;
            case "delegated" when me is { } m:
                q = q.Where(t => (t.DelegatorId == m || t.OriginalDelegatorId == m) && t.PrimaryAssigneeId != m);
                break;
            case "following" when me is { } m:
                q = q.Where(t => t.Followers.Any(x => x.EmployeeId == m));
                break;
            case "team":
            {
                var teams = v.TeamIds.ToList();
                var members = db.TeamMembers.Where(tm => teams.Contains(tm.TeamId)).Select(tm => tm.EmployeeId);
                q = q.Where(t => (t.TeamId != null && teams.Contains(t.TeamId.Value))
                    || t.Assignees.Any(a => a.RemovedAt == null && a.TeamId != null && teams.Contains(a.TeamId.Value))
                    || (t.PrimaryAssigneeId != null && members.Contains(t.PrimaryAssigneeId.Value)));
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            var p = TaskMetrics.Like(f.Text);
            const string esc = TaskMetrics.LikeEscape;
            q = q.Where(t => EF.Functions.Like(t.Number, p, esc) || EF.Functions.Like(t.Title, p, esc)
                || (t.Description != null && EF.Functions.Like(t.Description, p, esc))
                || (t.PrimaryAssignee != null && EF.Functions.Like(t.PrimaryAssignee.DisplayName, p, esc)));
        }

        if (f.Statuses is { Count: > 0 } statuses) q = q.Where(t => statuses.Contains(t.Status));
        if (f.Open == true) q = q.ActiveOnly();
        else if (f.Open == false) q = q.Where(t => t.Status == WorkStatus.Completed || t.Status == WorkStatus.Cancelled);
        if (f.AssignedToId is { } assignedTo) q = q.AssignedTo(assignedTo);
        if (f.AssignedById is { } assignedBy) q = q.Where(t => t.DelegatorId == assignedBy || t.OriginalDelegatorId == assignedBy);
        if (f.TeamId is { } teamId) q = q.ForTeam(db, teamId);
        if (f.DepartmentId is { } deptId) q = q.Where(t => t.DepartmentId == deptId);
        if (f.ProjectId is { } projectId) q = q.Where(t => t.ProjectId == projectId);
        if (f.Priorities is { Count: > 0 } priorities) q = q.Where(t => priorities.Contains(t.Priority));
        if (f.Criticalities is { Count: > 0 } crits) q = q.Where(t => crits.Contains(t.Criticality));
        if (f.MinCriticality is { } minCrit) q = q.Where(t => t.Criticality >= minCrit);
        if (f.Health is { Count: > 0 } health) q = q.Where(t => health.Contains(t.Health));
        if (f.DueFrom is { } dueFrom) q = q.Where(t => t.DueDate != null && t.DueDate >= dueFrom);
        if (f.DueTo is { } dueTo) q = q.Where(t => t.DueDate != null && t.DueDate <= dueTo);
        if (f.MinDaysOverdue is { } minDays) q = q.OverdueBy(c, minDays);
        if (f.Recurring == true) q = q.Where(t => t.RecurringDefinitionId != null);
        else if (f.Recurring == false) q = q.Where(t => t.RecurringDefinitionId == null);

        var now = c.Now;
        if (f.Overdue == true) q = q.OverdueAt(now);
        else if (f.Overdue == false)
            q = q.Where(t => t.DeadlineAt == null || t.DeadlineAt >= now || t.Status == WorkStatus.Completed || t.Status == WorkStatus.Cancelled
                || t.Status == WorkStatus.Draft || t.Status == WorkStatus.Deferred || t.Status == WorkStatus.CompletedByAssignee
                || t.Status == WorkStatus.CompletionAwaitingApproval);

        if (f.Blocked == true) q = q.BlockedOnly();
        else if (f.Blocked == false) q = q.Where(t => !t.IsBlocked && t.Status != WorkStatus.Blocked);
        if (f.NoResponse == true) q = q.NoResponseOnly();
        else if (f.NoResponse == false) q = q.Where(t => t.NoResponseSince == null);
        if (f.AwaitingApproval == true) q = q.AwaitingConfirmation();
        else if (f.AwaitingApproval == false) q = q.Where(t => t.Status != WorkStatus.CompletedByAssignee && t.Status != WorkStatus.CompletionAwaitingApproval);
        if (f.WaitingForCeo == true) q = q.WaitingFor(db, await Executives.IdsAsync(db, ct));
        if (f.TopLevelOnly == true) q = q.Where(t => t.ParentTaskId == null);
        if (!string.IsNullOrWhiteSpace(f.Tag))
        {
            var tag = f.Tag.Trim();
            q = q.Where(t => t.Tags.Any(x => x.Tag!.Name == tag));
        }
        if (f.CompletedFrom is { } cf)
        {
            var from = c.Utc(cf);
            q = q.Where(t => t.CompletedAt != null && t.CompletedAt >= from);
        }
        if (f.CompletedTo is { } cto)
        {
            var to = c.Utc(cto.AddDays(1));
            q = q.Where(t => t.CompletedAt != null && t.CompletedAt < to);
        }
        if (f.Bucket is { } bucket) q = ApplyBucket(q, bucket, c);
        return q;
    }

    /// <summary>Deadline bucket filter computed from the stored deadline instant and the local business date.</summary>
    public static IQueryable<WorkTask> ApplyBucket(IQueryable<WorkTask> q, DeadlineBucket bucket, QueryClock c) => bucket switch
    {
        DeadlineBucket.None => q.Where(t => t.DueDate == null || t.Status == WorkStatus.Completed || t.Status == WorkStatus.Cancelled),
        DeadlineBucket.Later => q.InProgressWork().NotPastDeadline(c.Now).Where(t => t.DueDate != null && t.DueDate > c.WeekEnd),
        DeadlineBucket.DueThisWeek => q.DueThisWeek(c),
        DeadlineBucket.DueTomorrow => q.DueBetween(c, c.Today.AddDays(1), c.Today.AddDays(1)),
        DeadlineBucket.DueToday => q.DueToday(c),
        DeadlineBucket.Overdue => q.MildlyOverdue(c),
        DeadlineBucket.SeverelyOverdue => q.SeverelyOverdue(c),
        _ => q,
    };

    public static IQueryable<WorkTask> Sort(IQueryable<WorkTask> q, string? sort) => (sort ?? "attention").Trim().ToLowerInvariant() switch
    {
        "due" => q.OrderBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.DueTime).ThenByDescending(t => t.AttentionScore).ThenBy(t => t.Id),
        "updated" => q.OrderByDescending(t => t.UpdatedAt).ThenBy(t => t.Id),
        "number" => q.OrderByDescending(t => t.NumberYear).ThenByDescending(t => t.NumberSequence).ThenBy(t => t.Id),
        "priority" => q.OrderByDescending(t => t.Priority).ThenByDescending(t => t.Criticality).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.Id),
        "completed" => q.OrderByDescending(t => t.CompletedAt).ThenBy(t => t.Id),
        _ => q.OrderByDescending(t => t.AttentionScore).ThenBy(t => t.DeadlineAt == null).ThenBy(t => t.DeadlineAt).ThenBy(t => t.Id),
    };

    /// <summary>Throws <see cref="NotFoundException"/> unless the task exists and is visible to the caller.</summary>
    public async Task EnsureVisibleAsync(Guid taskId, CancellationToken ct)
    {
        if (!await db.Tasks.AsNoTracking().Where(t => t.Id == taskId).VisibleTo(db, actor.Viewer).AnyAsync(ct)) throw new NotFoundException("Task");
    }

    // ═══════════════════════════════ Workload before delegation (spec §42) ═══════════════════════════════

    public async Task<List<WorkloadDto>> WorkloadAsync(IReadOnlyCollection<Guid>? employeeIds, Guid? teamId, CancellationToken ct)
    {
        var order = new List<Guid>();
        foreach (var id in employeeIds ?? []) if (!order.Contains(id)) order.Add(id);
        if (teamId is { } tid)
        {
            var team = await db.Teams.AsNoTracking().Where(t => t.Id == tid).Select(t => new { t.LeaderId, t.DeputyLeaderId }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Team");
            var members = await db.TeamMembers.AsNoTracking().Where(m => m.TeamId == tid).Select(m => m.EmployeeId).ToListAsync(ct);
            foreach (var id in new[] { team.LeaderId, team.DeputyLeaderId }.Where(x => x is not null).Select(x => x!.Value).Concat(members))
                if (!order.Contains(id)) order.Add(id);
        }
        if (order.Count == 0) return [];

        var c = await ClockAsync(ct);
        var people = await db.Employees.AsNoTracking().Where(e => order.Contains(e.Id))
            .Select(e => new { e.Id, e.DisplayName, e.JobTitle }).ToDictionaryAsync(e => e.Id, ct);
        var rows = await db.Tasks.AsNoTracking().ActiveOnly()
            .Where(t => (t.PrimaryAssigneeId != null && order.Contains(t.PrimaryAssigneeId.Value))
                || t.Assignees.Any(a => a.RemovedAt == null && a.EmployeeId != null && order.Contains(a.EmployeeId.Value)))
            .Select(t => new
            {
                t.PrimaryAssigneeId,
                People = t.Assignees.Where(a => a.RemovedAt == null && a.EmployeeId != null).Select(a => a.EmployeeId!.Value).ToList(),
                t.Status, t.DueDate, t.DeadlineAt, t.Criticality, t.IsBlocked, t.NoResponseSince,
            }).ToListAsync(ct);

        var result = new List<WorkloadDto>();
        foreach (var id in order)
        {
            if (!people.TryGetValue(id, out var person)) continue;
            var mine = rows.Where(r => r.PrimaryAssigneeId == id || r.People.Contains(id)).ToList();
            var awaiting = (Func<WorkStatus, bool>)TaskStatusRules.IsAwaitingConfirmation;
            var overdue = mine.Count(r => r.DeadlineAt is { } d && d < c.Now && !awaiting(r.Status));
            var dueThisWeek = mine.Count(r => r.DueDate is { } d && d >= c.Today && d <= c.WeekEnd && !(r.DeadlineAt is { } dl && dl < c.Now) && !awaiting(r.Status));
            var critical = mine.Count(r => r.Criticality >= Criticality.Critical);
            var blocked = mine.Count(r => r.IsBlocked || r.Status == WorkStatus.Blocked);
            var noResponse = mine.Count(r => r.NoResponseSince is not null);
            result.Add(new WorkloadDto(id, person.DisplayName, person.JobTitle, mine.Count, dueThisWeek, overdue, critical, blocked, noResponse,
                LoadLevel(mine.Count, dueThisWeek, overdue, critical, blocked)));
        }
        return result;
    }

    /// <summary>Light / Moderate / Heavy / Overloaded from open work weighted by urgency.</summary>
    public static string LoadLevel(int open, int dueThisWeek, int overdue, int critical, int blocked)
    {
        var score = open + dueThisWeek + 2 * overdue + 2 * critical + blocked;
        return score switch
        {
            < 6 => "Light",
            < 12 => "Moderate",
            < 20 => "Heavy",
            _ => "Overloaded",
        };
    }

    // ═══════════════════════════════ Global search (spec §52) ═══════════════════════════════

    public async Task<SearchResult> SearchAsync(string? query, CancellationToken ct, int perKind = 10)
    {
        var text = query?.Trim() ?? "";
        if (text.Length < 2) return new SearchResult(text, [], []);
        var p = TaskMetrics.Like(text);
        const string esc = TaskMetrics.LikeEscape;
        var v = actor.Viewer;
        var visible = db.Tasks.AsNoTracking().VisibleTo(db, v);
        var visibleIds = visible.Select(t => t.Id);
        var hits = new List<SearchHit>();

        // Tasks: an exact task number wins.
        var numbers = TaskNumbers.FindAll(text);
        var taskRows = await visible
            .Where(t => numbers.Contains(t.Number) || EF.Functions.Like(t.Number, p, esc) || EF.Functions.Like(t.Title, p, esc)
                || (t.Description != null && EF.Functions.Like(t.Description, p, esc)))
            .OrderByDescending(t => numbers.Contains(t.Number)).ThenByDescending(t => t.UpdatedAt).Take(perKind)
            .Select(t => new { t.Id, t.Number, t.Title, t.Description, t.Status, t.UpdatedAt }).ToListAsync(ct);
        hits.AddRange(taskRows.Select(t => new SearchHit("task", t.Id, $"{t.Number} — {t.Title}",
            Snippet(t.Description, text) ?? Text.Humanize(t.Status), t.Id, t.Number, t.UpdatedAt, $"/tasks/{t.Id}")));

        var commentRows = await db.TaskComments.AsNoTracking()
            .Where(x => !x.IsSystem && visibleIds.Contains(x.TaskId) && EF.Functions.Like(x.Body, p, esc))
            .OrderByDescending(x => x.CreatedAt).Take(perKind)
            .Select(x => new { x.Id, x.TaskId, x.Body, x.CreatedAt, Number = db.Tasks.Where(t => t.Id == x.TaskId).Select(t => t.Number).FirstOrDefault(),
                Author = x.Author != null ? x.Author.DisplayName : null }).ToListAsync(ct);
        hits.AddRange(commentRows.Select(x => new SearchHit("comment", x.Id, $"Comment by {x.Author ?? "system"} on {x.Number}",
            Snippet(x.Body, text) ?? "", x.TaskId, x.Number, x.CreatedAt, $"/tasks/{x.TaskId}")));

        var messageRows = await communications.VisibleMessages().AsNoTracking()
            .Where(m => (m.Subject != null && EF.Functions.Like(m.Subject, p, esc)) || EF.Functions.Like(m.BodyText, p, esc)
                || (m.SenderName != null && EF.Functions.Like(m.SenderName, p, esc)))
            .OrderByDescending(m => m.SentAt).Take(perKind)
            .Select(m => new { m.Id, m.Channel, m.Subject, m.SenderName, m.BodyPreview, m.SentAt,
                Body = m.BodyText.Length > 600 ? m.BodyText.Substring(0, 600) : m.BodyText }).ToListAsync(ct);
        hits.AddRange(messageRows.Select(m => new SearchHit(m.Channel == CommunicationChannel.Email ? "email" : "teams", m.Id,
            m.Channel == CommunicationChannel.Email ? $"Email: {m.Subject ?? "(no subject)"}" : $"Teams message from {m.SenderName ?? "unknown"}",
            Snippet(m.Body, text) ?? m.BodyPreview ?? "", null, null, m.SentAt, $"/messages/{m.Id}")));

        var commitmentRows = await db.Commitments.AsNoTracking()
            .Where(x => visibleIds.Contains(x.TaskId) && (EF.Functions.Like(x.OriginalText, p, esc) || (x.What != null && EF.Functions.Like(x.What, p, esc))))
            .OrderByDescending(x => x.CommitmentDate).Take(perKind)
            .Select(x => new { x.Id, x.TaskId, x.OriginalText, x.CommitmentDate, x.Status, x.CreatedAt,
                Number = db.Tasks.Where(t => t.Id == x.TaskId).Select(t => t.Number).FirstOrDefault(),
                Who = x.Employee != null ? x.Employee.DisplayName : null }).ToListAsync(ct);
        hits.AddRange(commitmentRows.Select(x => new SearchHit("commitment", x.Id,
            $"{x.Who ?? "Commitment"} — by {x.CommitmentDate:d MMM} ({Text.Humanize(x.Status).ToLowerInvariant()})",
            Snippet(x.OriginalText, text) ?? x.OriginalText, x.TaskId, x.Number, x.CreatedAt, $"/tasks/{x.TaskId}")));

        var projectRows = await db.Projects.AsNoTracking().VisibleProjects(v)
            .Where(x => EF.Functions.Like(x.Name, p, esc) || EF.Functions.Like(x.Code, p, esc) || (x.Description != null && EF.Functions.Like(x.Description, p, esc)))
            .OrderBy(x => x.Name).Take(perKind).Select(x => new { x.Id, x.Code, x.Name, x.Description, x.UpdatedAt }).ToListAsync(ct);
        hits.AddRange(projectRows.Select(x => new SearchHit("project", x.Id, $"{x.Code} — {x.Name}", Snippet(x.Description, text) ?? "", null, null,
            x.UpdatedAt, $"/projects/{x.Id}")));

        var peopleRows = await db.Employees.AsNoTracking().Where(e => e.IsActive
                && (EF.Functions.Like(e.DisplayName, p, esc) || EF.Functions.Like(e.Email, p, esc) || (e.JobTitle != null && EF.Functions.Like(e.JobTitle, p, esc))))
            .OrderBy(e => e.DisplayName).Take(perKind)
            .Select(e => new { e.Id, e.DisplayName, e.JobTitle, e.Email, Dept = e.Department != null ? e.Department.Name : null }).ToListAsync(ct);
        hits.AddRange(peopleRows.Select(e => new SearchHit("person", e.Id, e.DisplayName,
            string.Join(" · ", new[] { e.JobTitle, e.Dept, e.Email }.Where(s => !string.IsNullOrEmpty(s))), null, null, null, $"/people/{e.Id}")));

        var teamRows = await db.Teams.AsNoTracking().Where(t => t.IsActive && EF.Functions.Like(t.Name, p, esc)).OrderBy(t => t.Name).Take(perKind)
            .Select(t => new { t.Id, t.Name, Dept = t.Department != null ? t.Department.Name : null, Members = t.Members.Count }).ToListAsync(ct);
        hits.AddRange(teamRows.Select(t => new SearchHit("team", t.Id, t.Name, $"{t.Dept ?? "Team"} · {t.Members} member(s)", null, null, null, $"/teams/{t.Id}")));

        var deptRows = await db.Departments.AsNoTracking().Where(d => d.IsActive && (EF.Functions.Like(d.Name, p, esc) || EF.Functions.Like(d.Code, p, esc)))
            .OrderBy(d => d.Name).Take(perKind).Select(d => new { d.Id, d.Code, d.Name }).ToListAsync(ct);
        hits.AddRange(deptRows.Select(d => new SearchHit("department", d.Id, d.Name, $"Department {d.Code}", null, null, null, $"/tasks?departmentId={d.Id}")));

        var tagRows = await db.Tags.AsNoTracking().Where(t => EF.Functions.Like(t.Name, p, esc)).OrderBy(t => t.Name).Take(perKind)
            .Select(t => new { t.Id, t.Name, Count = db.TaskTags.Count(x => x.TagId == t.Id && visibleIds.Contains(x.TaskId)) }).ToListAsync(ct);
        hits.AddRange(tagRows.Where(t => t.Count > 0).Select(t => new SearchHit("tag", t.Id, $"#{t.Name}", $"{t.Count} task(s)", null, null, null,
            $"/tasks?tag={Uri.EscapeDataString(t.Name)}")));

        var attachmentRows = await db.Attachments.AsNoTracking()
            .Where(a => a.TaskId != null && visibleIds.Contains(a.TaskId.Value) && EF.Functions.Like(a.FileName, p, esc))
            .OrderByDescending(a => a.UploadedAt).Take(perKind)
            .Select(a => new { a.Id, a.TaskId, a.FileName, a.SizeBytes, a.UploadedAt, Number = db.Tasks.Where(t => t.Id == a.TaskId).Select(t => t.Number).FirstOrDefault() })
            .ToListAsync(ct);
        hits.AddRange(attachmentRows.Select(a => new SearchHit("attachment", a.Id, a.FileName, $"{a.Number} · {a.SizeBytes / 1024.0:0.#} KB", a.TaskId, a.Number,
            a.UploadedAt, $"/tasks/{a.TaskId}")));

        var counts = hits.GroupBy(h => h.Kind).ToDictionary(g => g.Key, g => g.Count());
        return new SearchResult(text, hits, counts);
    }

    /// <summary>A ~160 character window around the first occurrence of the search text.</summary>
    public static string? Snippet(string? body, string text)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var flat = body.ReplaceLineEndings(" ");
        var i = flat.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return NotificationService.Shorten(flat, 160);
        var start = Math.Max(0, i - 60);
        var len = Math.Min(flat.Length - start, 160);
        return (start > 0 ? "…" : "") + flat.Substring(start, len) + (start + len < flat.Length ? "…" : "");
    }

    // ═══════════════════════════════ Audit (spec §68) ═══════════════════════════════

    public async Task<List<AuditEntryDto>> AuditAsync(AuditQuery a, CancellationToken ct)
    {
        var v = actor.Viewer;
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (a.TaskId is { } taskId)
        {
            await EnsureVisibleAsync(taskId, ct);
            q = q.Where(x => x.TaskId == taskId);
        }
        else if (!actor.IsSystem && !v.IsAdmin && !v.IsExecutive)
        {
            var visibleIds = db.Tasks.VisibleTo(db, v).Select(t => t.Id);
            q = q.Where(x => x.TaskId != null && visibleIds.Contains(x.TaskId.Value));
        }
        if (!string.IsNullOrWhiteSpace(a.EntityType)) q = q.Where(x => x.EntityType == a.EntityType);
        if (!string.IsNullOrWhiteSpace(a.Action)) q = q.Where(x => x.Action == a.Action);
        if (a.From is not null || a.To is not null)
        {
            var cal = await calendars.GetDefaultAsync(ct);
            if (a.From is { } f) { var fu = cal.ToUtc(f, TimeOnly.MinValue); q = q.Where(x => x.At >= fu); }
            if (a.To is { } t) { var tu = cal.ToUtc(t.AddDays(1), TimeOnly.MinValue); q = q.Where(x => x.At < tu); }
        }
        var page = Math.Max(1, a.Page);
        var size = Math.Clamp(a.PageSize, 1, 500);
        var rows = await q.OrderByDescending(x => x.At).Skip((page - 1) * size).Take(size)
            .Select(x => new { x, Number = db.Tasks.Where(t => t.Id == x.TaskId).Select(t => t.Number).FirstOrDefault() }).ToListAsync(ct);
        return rows.Select(r => new AuditEntryDto(r.x.Id, r.x.At, r.x.ActorName, r.x.Action, r.x.EntityType, r.x.EntityId, r.x.TaskId, r.Number,
            r.x.Field, r.x.OldValue, r.x.NewValue, r.x.Details, r.x.Source)).ToList();
    }

    // ═══════════════════════════════ Lookups ═══════════════════════════════

    public async Task<Lookups> LookupsAsync(CancellationToken ct)
    {
        var execs = (await Executives.IdsAsync(db, ct)).ToHashSet();
        var people = await db.Employees.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.DisplayName)
            .Select(e => new { e.Id, e.DisplayName, e.JobTitle, e.Email, e.DepartmentId, Dept = e.Department != null ? e.Department.Name : null })
            .ToListAsync(ct);
        var teams = await db.Teams.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, t.DepartmentId, t.LeaderId, Members = t.Members.Select(m => m.EmployeeId).ToList() }).ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name).Select(d => new NamedRef(d.Id, d.Name)).ToListAsync(ct);
        var projects = await db.Projects.AsNoTracking().Where(p => p.IsActive).VisibleProjects(actor.Viewer).OrderBy(p => p.Name)
            .Select(p => new NamedRef(p.Id, p.Name)).ToListAsync(ct);
        var categories = await db.TaskCategories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).Select(c => new NamedRef(c.Id, c.Name)).ToListAsync(ct);
        var policies = await db.EscalationPolicies.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).Select(p => new NamedRef(p.Id, p.Name)).ToListAsync(ct);
        var tags = await db.Tags.AsNoTracking().OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(ct);
        var templates = await db.TaskTemplates.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Name).Select(t => new NamedRef(t.Id, t.Name)).ToListAsync(ct);
        return new Lookups(
            people.Select(e => new PersonLookup(e.Id, e.DisplayName, e.JobTitle, e.Email, e.DepartmentId, e.Dept, execs.Contains(e.Id))).ToList(),
            teams.Select(t => new TeamLookup(t.Id, t.Name, t.DepartmentId, t.LeaderId, t.Members)).ToList(),
            departments, projects, categories, policies, tags, templates);
    }

    // ═══════════════════════════════ Helpers ═══════════════════════════════

    private async Task<Dictionary<Guid, PersonRef>> PeopleAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var set = ids.Where(x => x is not null && x != Guid.Empty).Select(x => x!.Value).Distinct().ToList();
        if (set.Count == 0) return [];
        return await db.Employees.AsNoTracking().Where(e => set.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new PersonRef(e.Id, e.DisplayName, e.JobTitle, e.Email), ct);
    }
}
