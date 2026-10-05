using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Services;

/// <summary>
/// Task lifecycle (spec §4–§8, §16, §28–§33, §43–§45, §57): fast delegation, subtasks, assignment and the
/// delegation chain, acknowledgement, status changes, comments (with commitment detection), completion with
/// evidence, approval, extensions, snooze, dependencies and quick actions. Every change is audited, may queue
/// notifications in the same transaction, and refreshes the task's health / attention state.
/// </summary>
public sealed class TaskService(
    FollowUpDbContext db,
    ActorContext actor,
    IClock clock,
    CalendarProvider calendars,
    AuditService audit,
    NotificationService notifications,
    MonitoringService monitoring,
    ICommitmentExtractionService commitmentExtractor,
    IOptions<FollowUpOptions> options) : ITaskService
{
    private readonly FollowUpOptions _o = options.Value;

    // ═══════════════════════════════ Create ═══════════════════════════════

    public Task<WorkTask> CreateAsync(CreateTaskRequest r, CancellationToken ct) => CreateAsync(r, ct, null);

    /// <param name="onBehalfOf">When the system creates a task (recurrence, meetings, templates), the person it delegates for.</param>
    public async Task<WorkTask> CreateAsync(CreateTaskRequest r, CancellationToken ct, Guid? onBehalfOf, Action<WorkTask>? configure = null)
    {
        if (string.IsNullOrWhiteSpace(r.Title)) throw new ValidationException("title_required", "A task needs a title.");
        var delegatorId = actor.EmployeeId ?? onBehalfOf;
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        var today = cal.Today(now);

        WorkTask? parent = null;
        if (r.ParentTaskId is { } pid)
        {
            parent = await db.Tasks.Include(t => t.Assignees).FirstOrDefaultAsync(t => t.Id == pid, ct) ?? throw new NotFoundException("Parent task");
            await EnsureVisibleAsync(parent, ct);
            if (!actor.IsSystem && !await CanManageAsync(parent, ct) && !await IsResponsibleAsync(parent, actor.EmployeeId!.Value, ct))
                throw new ForbiddenException("Only the delegator, managers or the responsible people can add subtasks.");
        }

        // Team assignment without a named person → the team leader is primarily responsible (spec §5).
        var primaryId = r.PrimaryAssigneeId;
        var teamIds = r.TeamIds.Distinct().ToList();
        if (r.TeamId is { } t0 && !teamIds.Contains(t0) && primaryId is null) teamIds.Add(t0);
        var teams = teamIds.Count > 0 ? await db.Teams.Where(t => teamIds.Contains(t.Id)).ToListAsync(ct) : [];
        if (primaryId is null && teams.FirstOrDefault()?.LeaderId is { } leader) primaryId = leader;
        if (primaryId is null && !r.IsDraft)
            throw new ValidationException("assignee_required", "Choose the primary responsible person or a team with a leader.");

        var primary = primaryId is { } p ? await db.Employees.FirstOrDefaultAsync(e => e.Id == p && e.IsActive, ct) ?? throw new ValidationException("assignee_invalid", "Primary responsible person not found.") : null;

        var (year, seq, number) = await AllocateNumberAsync(today.Year, ct);
        var requiresAck = r.RequiresAcknowledgement ?? r.Criticality >= _o.AcknowledgementFrom;
        var teamId = r.TeamId ?? teams.FirstOrDefault()?.Id ?? parent?.TeamId;
        var departmentId = r.DepartmentId ?? teams.FirstOrDefault()?.DepartmentId ?? parent?.DepartmentId ?? primary?.DepartmentId;

        var task = new WorkTask
        {
            Number = number,
            NumberYear = year,
            NumberSequence = seq,
            ParentTaskId = parent?.Id,
            RootTaskId = parent is null ? null : parent.RootTaskId ?? parent.Id,
            Depth = parent is null ? 0 : parent.Depth + 1,
            SortOrder = parent is null ? 0 : await db.Tasks.CountAsync(t => t.ParentTaskId == parent.Id, ct),
            Title = r.Title.Trim(),
            Description = r.Description,
            Objective = r.Objective,
            ExpectedResult = r.ExpectedResult,
            Deliverable = r.Deliverable,
            SuccessCriteria = r.SuccessCriteria,
            Links = r.Links,
            DepartmentId = departmentId,
            TeamId = teamId,
            PrimaryAssigneeId = primary?.Id,
            AccountableManagerId = r.AccountableManagerId,
            DelegatorId = delegatorId,
            OriginalDelegatorId = delegatorId,
            CreatedById = delegatorId,
            ProjectId = r.ProjectId ?? parent?.ProjectId,
            CategoryId = r.CategoryId,
            Priority = r.Priority,
            Criticality = r.Criticality,
            Visibility = parent is not null && r.Visibility == TaskVisibility.Normal ? parent.Visibility : r.Visibility,
            Source = r.Source,
            Status = r.IsDraft ? WorkStatus.Draft : requiresAck ? WorkStatus.AcknowledgementPending : WorkStatus.Assigned,
            Acknowledgement = r.IsDraft || !requiresAck ? AcknowledgementState.NotRequired : AcknowledgementState.Pending,
            AssignedAt = r.IsDraft ? null : now,
            StartDate = r.StartDate,
            DueDate = r.DueDate,
            DueTime = r.DueTime,
            HardDeadline = r.HardDeadline,
            ReviewDate = r.ReviewDate,
            OriginalDueDate = r.DueDate,
            FollowUp = r.FollowUp ?? FollowUpSchedule.NoneSchedule,
            RequiresApproval = ApprovalRules.IsRequired(r.RequiresApproval ?? false, r.Criticality),
            CustomerImpact = r.CustomerImpact,
            CustomerName = r.CustomerName,
            FinancialImpact = r.FinancialImpact,
            NotifyCeoOnDelay = r.NotifyCeoOnDelay,
            EscalationPolicyId = r.EscalationPolicyId,
            MeetingActionItemId = r.MeetingActionItemId,
            SourceMessageId = r.SourceMessageId,
            LastActivityAt = now,
            CreatedAt = now,
        };
        if (r.RequiresApproval == false && r.Criticality < Criticality.Critical) task.RequiresApproval = false;
        task.DeadlineAt = task.DueDate is { } dd ? cal.DeadlineUtc(dd, task.DueTime) : null;
        if (!r.IsDraft) task.NextFollowUpAt = FollowUpScheduler.Next(task.FollowUp, now, task.StartDate ?? today, cal);

        // Assignees (spec §5)
        if (primary is not null) AddAssignee(task, primary.Id, null, AssigneeRole.Primary, now);
        foreach (var id in r.AdditionalAssigneeIds.Where(x => x != primary?.Id).Distinct()) AddAssignee(task, id, null, AssigneeRole.Additional, now);
        foreach (var team in teams)
        {
            AddAssignee(task, null, team.Id, AssigneeRole.Additional, now);
            if (r.IncludeTeamLeader && team.LeaderId is { } tl && task.Assignees.All(a => a.EmployeeId != tl))
                AddAssignee(task, tl, team.Id, AssigneeRole.TeamLeader, now);
        }
        foreach (var id in r.SupportingMemberIds.Where(x => task.Assignees.All(a => a.EmployeeId != x)).Distinct())
            AddAssignee(task, id, null, AssigneeRole.Supporting, now);
        foreach (var id in r.FollowerIds.Distinct()) task.Followers.Add(new TaskFollower { TaskId = task.Id, EmployeeId = id, AddedAt = now });
        await ApplyTagsAsync(task, r.Tags, ct);
        configure?.Invoke(task);

        db.Tasks.Add(task);

        foreach (var d in r.Dependencies) await AddDependencyInternalAsync(task, d, now, ct);
        if (r.AttachmentIds.Count > 0)
            await db.Attachments.Where(a => r.AttachmentIds.Contains(a.Id) && a.TaskId == null).ForEachAsync(a => a.TaskId = task.Id, ct);

        if (primary is not null)
            db.TaskDelegations.Add(new TaskDelegation { TaskId = task.Id, Sequence = 1, FromEmployeeId = delegatorId, ToEmployeeId = primary.Id, DelegatedAt = now, Note = "Initial delegation" });
        db.TaskStatusHistory.Add(new TaskStatusHistory { TaskId = task.Id, FromStatus = null, ToStatus = task.Status, ChangedAt = now, ChangedById = delegatorId, Source = actor.Source, Reason = "Task created" });
        if (r.MeetingActionItemId is { } mai && await db.MeetingActionItems.FirstOrDefaultAsync(x => x.Id == mai, ct) is { } item)
        {
            item.TaskId = task.Id;
            item.Status = MeetingActionStatus.TaskCreated;
        }

        audit.Record("TaskCreated", nameof(WorkTask), task.Id, task.Id, details: $"{task.Number} {task.Title}");
        if (primary is not null) audit.Record("TaskAssigned", nameof(WorkTask), task.Id, task.Id, "PrimaryAssignee", null, primary.DisplayName);

        if (!r.IsDraft) await NotifyAssignmentAsync(task, primary, teams, isReassignment: false, ct);

        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskCreated, task.Id);
        await db.SaveChangesAsync(ct);
        return task;
    }

    private static void AddAssignee(WorkTask task, Guid? employeeId, Guid? teamId, AssigneeRole role, DateTime now) =>
        task.Assignees.Add(new TaskAssignee { TaskId = task.Id, EmployeeId = employeeId, TeamId = teamId, Role = role, AssignedAt = now });

    private async Task NotifyAssignmentAsync(WorkTask task, Employee? primary, List<Team> teams, bool isReassignment, CancellationToken ct)
    {
        var delegator = actor.IsSystem ? "the Follow-up Officer" : actor.Name;
        var ackText = task.Acknowledgement == AcknowledgementState.Pending
            ? "Please acknowledge the task: Accept, Need Clarification, Cannot Accept or Propose a Different Deadline."
            : null;
        if (primary is not null)
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = $"assign:{task.Id:N}:{primary.Id:N}:{task.Version}:{(isReassignment ? "re" : "new")}",
                Category = task.Acknowledgement == AcknowledgementState.Pending ? NotificationCategory.AcknowledgementRequest : NotificationCategory.Assignment,
                Task = task,
                RecipientIds = [primary.Id],
                Headline = isReassignment ? $"has been delegated to you by {delegator}." : $"has been assigned to you by {delegator}.",
                Message = ackText,
                Reason = "You are the primary responsible person.",
            }, ct);

        var others = task.Assignees.Where(a => a.EmployeeId is { } e && e != primary?.Id && a.RemovedAt == null).Select(a => a.EmployeeId!.Value).Distinct().ToList();
        if (others.Count > 0)
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = $"assign:{task.Id:N}:others:{task.Version}",
                Category = NotificationCategory.Assignment,
                Task = task,
                RecipientIds = others,
                Headline = $"includes you as a contributor ({delegator}).",
                Reason = "You are an assignee or supporting member.",
            }, ct);

        foreach (var team in teams)
            await notifications.EnqueueChannelAsync($"assign:{task.Id:N}", task, team, NotificationCategory.Assignment, "has been assigned to the team.", null, ct);
    }

    // ═══════════════════════════════ Update ═══════════════════════════════

    public async Task<WorkTask> UpdateAsync(Guid id, UpdateTaskRequest r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        await RequireManageAsync(task, ct);
        var cal = await calendars.GetDefaultAsync(ct);
        var now = clock.UtcNow;

        void Set<T>(string field, T current, T? value, Action<T> apply, string action = "TaskUpdated") where T : struct
        {
            if (value is not { } v || EqualityComparer<T>.Default.Equals(current, v)) return;
            audit.Change(action, task, field, current, v, r.ChangeReason);
            apply(v);
        }
        void SetText(string field, string? current, string? value, Action<string?> apply)
        {
            if (value is null || value == current) return;
            audit.Change("TaskUpdated", task, field, current, value, r.ChangeReason);
            apply(string.IsNullOrWhiteSpace(value) ? null : value);
        }

        if (r.Title is { } title && !string.IsNullOrWhiteSpace(title) && title != task.Title) { audit.Change("TaskUpdated", task, "Title", task.Title, title); task.Title = title.Trim(); }
        SetText("Description", task.Description, r.Description, v => task.Description = v);
        SetText("Objective", task.Objective, r.Objective, v => task.Objective = v);
        SetText("ExpectedResult", task.ExpectedResult, r.ExpectedResult, v => task.ExpectedResult = v);
        SetText("Deliverable", task.Deliverable, r.Deliverable, v => task.Deliverable = v);
        SetText("SuccessCriteria", task.SuccessCriteria, r.SuccessCriteria, v => task.SuccessCriteria = v);
        SetText("CustomerName", task.CustomerName, r.CustomerName, v => task.CustomerName = v);
        Set("Priority", task.Priority, r.Priority, v => task.Priority = v, "PriorityChanged");
        Set("Criticality", task.Criticality, r.Criticality, v => task.Criticality = v, "CriticalityChanged");
        Set("Visibility", task.Visibility, r.Visibility, v => task.Visibility = v, "VisibilityChanged");
        if (r.ProjectId is { } proj && proj != task.ProjectId) { audit.Change("TaskUpdated", task, "Project", task.ProjectId, proj); task.ProjectId = proj == Guid.Empty ? null : proj; }
        if (r.CategoryId is { } cat && cat != task.CategoryId) { audit.Change("TaskUpdated", task, "Category", task.CategoryId, cat); task.CategoryId = cat == Guid.Empty ? null : cat; }
        if (r.DepartmentId is { } dep && dep != task.DepartmentId) { audit.Change("TaskUpdated", task, "Department", task.DepartmentId, dep); task.DepartmentId = dep == Guid.Empty ? null : dep; }
        if (r.TeamId is { } team && team != task.TeamId) { audit.Change("TaskUpdated", task, "Team", task.TeamId, team); task.TeamId = team == Guid.Empty ? null : team; }
        if (r.AccountableManagerId is { } am && am != task.AccountableManagerId) { audit.Change("TaskUpdated", task, "AccountableManager", task.AccountableManagerId, am); task.AccountableManagerId = am == Guid.Empty ? null : am; }
        Set("StartDate", task.StartDate ?? default, r.StartDate, v => task.StartDate = v);
        Set("HardDeadline", task.HardDeadline ?? default, r.HardDeadline, v => task.HardDeadline = v, "DeadlineChanged");
        Set("ReviewDate", task.ReviewDate ?? default, r.ReviewDate, v => task.ReviewDate = v);
        Set("RequiresApproval", task.RequiresApproval, r.RequiresApproval, v => task.RequiresApproval = v);
        Set("CustomerImpact", task.CustomerImpact, r.CustomerImpact, v => task.CustomerImpact = v);
        Set("FinancialImpact", task.FinancialImpact ?? 0, r.FinancialImpact, v => task.FinancialImpact = v);
        Set("NotifyCeoOnDelay", task.NotifyCeoOnDelay, r.NotifyCeoOnDelay, v => task.NotifyCeoOnDelay = v);
        if (r.EscalationPolicyId is { } pol && pol != task.EscalationPolicyId) { audit.Change("TaskUpdated", task, "EscalationPolicy", task.EscalationPolicyId, pol); task.EscalationPolicyId = pol == Guid.Empty ? null : pol; }
        if (r.Links is { } links) task.Links = links;
        if (r.Tags is { } tags) await ApplyTagsAsync(task, tags, ct, replace: true);

        if (r.DueDate is { } due && due != task.DueDate)
            ApplyDueDateChange(task, due, r.DueTime ?? task.DueTime, r.ChangeReason ?? "Due date changed", cal);
        else if (r.ClearDueTime || (r.DueTime is { } dt && dt != task.DueTime))
        {
            audit.Change("DeadlineChanged", task, "DueTime", task.DueTime, r.ClearDueTime ? null : r.DueTime);
            task.DueTime = r.ClearDueTime ? null : r.DueTime;
            task.DeadlineAt = task.DueDate is { } d ? cal.DeadlineUtc(d, task.DueTime) : null;
        }

        if (r.FollowUp is { } fu && System.Text.Json.JsonSerializer.Serialize(fu) != System.Text.Json.JsonSerializer.Serialize(task.FollowUp))
        {
            audit.Change("FollowUpChanged", task, "FollowUp", task.FollowUp.Describe(), fu.Describe());
            task.FollowUp = fu;
            task.NextFollowUpAt = task.IsClosed ? null : FollowUpScheduler.Next(fu, now, task.StartDate ?? cal.Today(now), cal);
        }

        task.LastActivityAt = now;
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    private void ApplyDueDateChange(WorkTask task, DateOnly due, TimeOnly? dueTime, string reason, BusinessCalendar cal)
    {
        audit.Change("DeadlineChanged", task, "DueDate", task.DueDate, due, reason);
        task.OriginalDueDate ??= task.DueDate ?? due;
        task.DueDate = due;
        task.DueTime = dueTime;
        task.DueDateChangeCount++;
        task.DeadlineAt = cal.DeadlineUtc(due, dueTime);
    }

    // ═══════════════════════════════ Delegation chain (spec §43) ═══════════════════════════════

    public async Task<WorkTask> ReassignAsync(Guid id, ReassignRequest r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        var me = actor.EmployeeId;
        var isPrimary = me is not null && me == task.PrimaryAssigneeId;
        if (!isPrimary) await RequireManageAsync(task, ct);
        if (r.NewPrimaryAssigneeId == task.PrimaryAssigneeId) return task;
        var newPrimary = await db.Employees.FirstOrDefaultAsync(e => e.Id == r.NewPrimaryAssigneeId && e.IsActive, ct)
            ?? throw new ValidationException("assignee_invalid", "Person not found.");
        var now = clock.UtcNow;
        var previous = task.PrimaryAssigneeId;
        var previousName = previous is { } pid ? await db.Employees.Where(e => e.Id == pid).Select(e => e.DisplayName).FirstOrDefaultAsync(ct) : null;

        foreach (var a in task.Assignees.Where(a => a.Role == AssigneeRole.Primary && a.RemovedAt == null))
        {
            if (r.KeepPreviousAsSupporting) a.Role = AssigneeRole.Supporting;
            else a.RemovedAt = now;
        }
        var existing = task.Assignees.FirstOrDefault(a => a.EmployeeId == newPrimary.Id && a.RemovedAt == null);
        if (existing is not null) existing.Role = AssigneeRole.Primary;
        else AddAssignee(task, newPrimary.Id, null, AssigneeRole.Primary, now);

        var sequence = await db.TaskDelegations.Where(d => d.TaskId == task.Id).MaxAsync(d => (int?)d.Sequence, ct) ?? 0;
        db.TaskDelegations.Add(new TaskDelegation { TaskId = task.Id, Sequence = sequence + 1, FromEmployeeId = me ?? previous, ToEmployeeId = newPrimary.Id, DelegatedAt = now, Note = r.Note });

        task.PrimaryAssigneeId = newPrimary.Id;
        task.DelegatorId = me ?? task.DelegatorId;          // original delegator never changes
        task.AssignedAt = now;
        task.RemindersSinceResponse = 0;
        task.NoResponseSince = null;
        if (task.Acknowledgement != AcknowledgementState.NotRequired && task.IsOpen)
        {
            task.Acknowledgement = AcknowledgementState.Pending;
            ChangeStatusInternal(task, WorkStatus.AcknowledgementPending, $"Re-delegated to {newPrimary.DisplayName}", now);
        }
        audit.Record("Reassigned", nameof(WorkTask), task.Id, task.Id, "PrimaryAssignee", previousName, newPrimary.DisplayName, r.Note);
        AddSystemComment(task, $"Delegated from {previousName ?? "—"} to {newPrimary.DisplayName}{(string.IsNullOrWhiteSpace(r.Note) ? "" : $": {r.Note}")}", now);

        await NotifyAssignmentAsync(task, newPrimary, [], isReassignment: true, ct);
        if (previous is { } prevId && prevId != me)
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = $"reassigned:{task.Id:N}:{prevId:N}:{now.Ticks}",
                Category = NotificationCategory.StatusChange,
                Task = task,
                RecipientIds = [prevId],
                Headline = $"has been delegated to {newPrimary.DisplayName}.",
                Channels = NotifyChannels.InApp,
            }, ct);

        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    public async Task<WorkTask> AddAssigneeAsync(Guid id, AssigneeChange c, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        await RequireManageAsync(task, ct);
        if (c.Role == AssigneeRole.Primary) throw new ValidationException("use_reassign", "Use reassign to change the primary responsible person.");
        if (task.Assignees.Any(a => a.RemovedAt == null && a.EmployeeId == c.EmployeeId && a.TeamId == c.TeamId)) return task;
        var now = clock.UtcNow;
        AddAssignee(task, c.EmployeeId, c.TeamId, c.Role, now);
        var name = c.EmployeeId is { } e ? await db.Employees.Where(x => x.Id == e).Select(x => x.DisplayName).FirstOrDefaultAsync(ct)
            : await db.Teams.Where(x => x.Id == c.TeamId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        audit.Record("AssigneeAdded", nameof(WorkTask), task.Id, task.Id, c.Role.ToString(), null, name);
        if (c.EmployeeId is { } emp)
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = $"assign:{task.Id:N}:{emp:N}:{now.Ticks}",
                Category = NotificationCategory.Assignment,
                Task = task,
                RecipientIds = [emp],
                Headline = $"includes you as {Text.Humanize(c.Role).ToLowerInvariant()}.",
            }, ct);
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    public async Task<WorkTask> RemoveAssigneeAsync(Guid id, Guid assigneeRowId, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        await RequireManageAsync(task, ct);
        var row = task.Assignees.FirstOrDefault(a => a.Id == assigneeRowId && a.RemovedAt == null) ?? throw new NotFoundException("Assignee");
        if (row.Role == AssigneeRole.Primary) throw new ValidationException("primary_required", "Reassign the task instead of removing the primary responsible person.");
        row.RemovedAt = clock.UtcNow;
        audit.Record("AssigneeRemoved", nameof(WorkTask), task.Id, task.Id, row.Role.ToString(), row.EmployeeId?.ToString() ?? row.TeamId?.ToString(), null);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    public async Task SetFollowerAsync(Guid id, Guid employeeId, bool follow, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        if (actor.EmployeeId != employeeId) await RequireManageAsync(task, ct);
        var existing = task.Followers.FirstOrDefault(f => f.EmployeeId == employeeId);
        if (follow && existing is null)
        {
            task.Followers.Add(new TaskFollower { TaskId = task.Id, EmployeeId = employeeId, AddedAt = clock.UtcNow });
            audit.Record("FollowerAdded", nameof(WorkTask), task.Id, task.Id, "Follower", null, employeeId.ToString());
        }
        else if (!follow && existing is not null)
        {
            db.TaskFollowers.Remove(existing);
            audit.Record("FollowerRemoved", nameof(WorkTask), task.Id, task.Id, "Follower", employeeId.ToString(), null);
        }
        await SaveAsync(ct);
    }

    // ═══════════════════════════════ Acknowledgement (spec §8) ═══════════════════════════════

    public async Task<WorkTask> AcknowledgeAsync(Guid id, AcknowledgeRequest r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        var me = actor.EmployeeId ?? throw new ForbiddenException("Sign in to acknowledge.");
        if (!await IsResponsibleAsync(task, me, ct)) throw new ForbiddenException("Only the responsible people can acknowledge this task.");
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);

        db.TaskAcknowledgements.Add(new TaskAcknowledgement { TaskId = task.Id, EmployeeId = me, Response = r.Response, ProposedDueDate = r.ProposedDueDate, Comment = r.Comment, RespondedAt = now });
        task.AcknowledgedAt = now;
        task.AcknowledgedById = me;
        task.AcknowledgementComment = r.Comment;
        RecordAssigneeActivity(task, now, "acknowledgement");

        string headline;
        switch (r.Response)
        {
            case AcknowledgementResponse.Accept:
                task.Acknowledgement = AcknowledgementState.Accepted;
                if (task.Status is WorkStatus.AcknowledgementPending or WorkStatus.Assigned) ChangeStatusInternal(task, WorkStatus.Accepted, "Accepted by assignee", now);
                headline = $"was accepted by {actor.Name}.";
                break;
            case AcknowledgementResponse.NeedClarification:
                task.Acknowledgement = AcknowledgementState.ClarificationRequested;
                if (task.Status == WorkStatus.AcknowledgementPending) ChangeStatusInternal(task, WorkStatus.WaitingForInformation, "Clarification requested", now);
                headline = $"needs clarification — {actor.Name} asked a question.";
                break;
            case AcknowledgementResponse.CannotAccept:
                task.Acknowledgement = AcknowledgementState.Declined;
                headline = $"could not be accepted by {actor.Name}.";
                break;
            default:
                if (r.ProposedDueDate is not { } proposed) throw new ValidationException("date_required", "Propose a new deadline.");
                task.Acknowledgement = AcknowledgementState.DeadlineProposed;
                await CreateExtensionAsync(task, me, proposed, r.Comment ?? "Proposed a different deadline when acknowledging", CommunicationSource.Acknowledgement, now, ct);
                headline = $"was acknowledged by {actor.Name} with a different deadline proposed ({proposed:d MMM}).";
                break;
        }
        if (!string.IsNullOrWhiteSpace(r.Comment))
            db.TaskComments.Add(new TaskComment { TaskId = task.Id, AuthorId = me, Body = $"[{Text.Humanize(r.Response)}] {r.Comment}", CreatedAt = now });
        audit.Record("Acknowledged", nameof(WorkTask), task.Id, task.Id, "Acknowledgement", null, r.Response.ToString(), r.Comment);
        task.NextFollowUpAt ??= FollowUpScheduler.Next(task.FollowUp, now, task.StartDate ?? cal.Today(now), cal);

        var notify = new[] { task.DelegatorId }.Where(x => x is not null && x != me).Select(x => x!.Value).ToList();
        if (notify.Count > 0)
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = $"ack:{task.Id:N}:{now.Ticks}",
                Category = NotificationCategory.StatusChange,
                Task = task,
                RecipientIds = notify,
                Headline = headline,
                Message = r.Comment,
                Channels = r.Response == AcknowledgementResponse.Accept ? NotifyChannels.InApp : NotifyChannels.InApp | NotifyChannels.Email,
            }, ct);

        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    // ═══════════════════════════════ Status & progress ═══════════════════════════════

    public async Task<WorkTask> ChangeStatusAsync(Guid id, StatusChangeRequest r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        var manage = await CanManageAsync(task, ct);
        var responsible = actor.EmployeeId is { } me && await IsResponsibleAsync(task, me, ct);
        if (!manage && !responsible && !actor.IsSystem) throw new ForbiddenException("You cannot change this task's status.");
        if (r.Status is WorkStatus.CompletedByAssignee or WorkStatus.CompletionAwaitingApproval or WorkStatus.Completed)
            throw new ValidationException("use_complete", "Use 'Mark complete' (with evidence) or the approval action.");
        if (r.Status is WorkStatus.Cancelled or WorkStatus.Deferred or WorkStatus.Draft && !manage && !actor.IsSystem)
            throw new ForbiddenException("Only the delegator or a manager can cancel or defer a task.");
        if (!TaskStatusRules.CanTransition(task.Status, r.Status))
            throw new ConflictException("invalid_transition", $"Cannot move from {Text.Humanize(task.Status)} to {Text.Humanize(r.Status)}.");

        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        var wasDraft = task.Status == WorkStatus.Draft;
        ChangeStatusInternal(task, r.Status, r.Reason, now);
        if (r.ProgressPercent is { } pct) task.ProgressPercent = Math.Clamp(pct, 0, 100);

        if (TaskStatusRules.IsWaiting(r.Status))
        {
            task.IsBlocked = true;
            task.BlockedSince ??= now;
            task.BlockedReason = r.Reason ?? task.BlockedReason;
        }
        else if (task.IsBlocked && !await HasOpenBlockingDependenciesAsync(task, ct))
        {
            task.IsBlocked = false;
            task.BlockedSince = null;
            task.BlockedReason = null;
        }
        if (r.Status == WorkStatus.Cancelled)
        {
            task.ClosedAt = now;
            task.NextFollowUpAt = null;
            await CloseCommitmentsAsync(task, CommitmentStatus.Cancelled, "Task cancelled", now, ct);
        }
        if (wasDraft)
        {
            task.AssignedAt = now;
            task.NextFollowUpAt = FollowUpScheduler.Next(task.FollowUp, now, task.StartDate ?? cal.Today(now), cal);
            var primary = task.PrimaryAssigneeId is { } p ? await db.Employees.FindAsync([p], ct) : null;
            await NotifyAssignmentAsync(task, primary, [], false, ct);
        }
        if (r.Status == WorkStatus.InProgress && task.Status == WorkStatus.InProgress && task.Acknowledgement == AcknowledgementState.Pending)
        {
            task.Acknowledgement = AcknowledgementState.Accepted;
            task.AcknowledgedAt = now;
        }
        if (task.Status is WorkStatus.InProgress && task.IsClosed is false && r.Status == WorkStatus.InProgress && task.ClosedAt is not null)
            task.ClosedAt = null;

        if (responsible) RecordAssigneeActivity(task, now, "status change");
        else task.LastActivityAt = now;
        if (!string.IsNullOrWhiteSpace(r.Reason))
            db.TaskComments.Add(new TaskComment { TaskId = task.Id, AuthorId = actor.EmployeeId, Body = $"Status → {Text.Humanize(r.Status)}: {r.Reason}", CreatedAt = now, IsSystem = actor.IsSystem });

        // Blocked / waiting notifies the delegator (spec §10, §39).
        if (TaskStatusRules.IsWaiting(r.Status) && task.DelegatorId is { } del && del != actor.EmployeeId)
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = $"status:{task.Id:N}:{r.Status}:{now.Ticks}",
                Category = NotificationCategory.StatusChange,
                Task = task,
                RecipientIds = [del],
                Headline = $"is now {Text.Humanize(r.Status).ToLowerInvariant()}.",
                Message = r.Reason,
                Channels = task.Criticality >= Criticality.Important ? NotifyChannels.InApp | NotifyChannels.Email : NotifyChannels.InApp,
            }, ct);

        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    public async Task<WorkTask> UpdateProgressAsync(Guid id, ProgressRequest r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        var me = actor.EmployeeId;
        var responsible = me is { } m && await IsResponsibleAsync(task, m, ct);
        if (!responsible && !await CanManageAsync(task, ct)) throw new ForbiddenException("You cannot update this task.");
        var now = clock.UtcNow;
        var pct = Math.Clamp(r.ProgressPercent, 0, 100);
        audit.Change("ProgressUpdated", task, "Progress", task.ProgressPercent, pct, r.Note);
        task.ProgressPercent = pct;
        if (task.Status is WorkStatus.Accepted or WorkStatus.Assigned or WorkStatus.AcknowledgementPending && pct > 0)
            ChangeStatusInternal(task, pct >= 100 ? WorkStatus.PartiallyCompleted : WorkStatus.InProgress, "Progress reported", now);
        if (!string.IsNullOrWhiteSpace(r.Note))
        {
            var comment = new TaskComment { TaskId = task.Id, AuthorId = me, Body = $"Progress {pct}% — {r.Note}", CreatedAt = now };
            db.TaskComments.Add(comment);
            task.LastUpdateSummary = NotificationService.Shorten(r.Note!, 300);
            await ExtractCommitmentsAsync(task, r.Note!, me, CommunicationSource.Comment, null, comment.Id, now, ct);
        }
        if (responsible) RecordAssigneeActivity(task, now, "progress update"); else task.LastActivityAt = now;
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    internal void ChangeStatusInternal(WorkTask task, WorkStatus to, string? reason, DateTime now)
    {
        if (task.Status == to) return;
        db.TaskStatusHistory.Add(new TaskStatusHistory { TaskId = task.Id, FromStatus = task.Status, ToStatus = to, ChangedAt = now, ChangedById = actor.EmployeeId, Reason = reason, Source = actor.Source });
        audit.Change("StatusChanged", task, "Status", task.Status, to, reason);
        if (to == WorkStatus.InProgress && task.StartedAt is null) task.StartedAt = now;
        task.Status = to;
    }

    // ═══════════════════════════════ Comments & commitments (spec §28) ═══════════════════════════════

    public async Task<TaskComment> CommentAsync(Guid id, CommentRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Body)) throw new ValidationException("body_required", "Write a comment.");
        var task = await LoadAsync(id, ct);
        var me = actor.EmployeeId;
        var now = clock.UtcNow;
        var comment = new TaskComment { TaskId = task.Id, AuthorId = me, Body = r.Body.Trim(), CreatedAt = now, IsSystem = actor.IsSystem };
        db.TaskComments.Add(comment);
        audit.Record("Comment", nameof(TaskComment), comment.Id, task.Id, details: NotificationService.Shorten(comment.Body, 300));

        var responsible = me is { } m && await IsResponsibleAsync(task, m, ct);
        if (responsible)
        {
            RecordAssigneeActivity(task, now, "comment");
            task.LastUpdateSummary = NotificationService.Shorten(comment.Body, 300);
        }
        else task.LastActivityAt = now;

        await ExtractCommitmentsAsync(task, comment.Body, me, CommunicationSource.Comment, null, comment.Id, now, ct);

        // Notify the other side of the delegation (in-app only; comments are not spam-worthy).
        var recipients = new HashSet<Guid>();
        if (responsible) { if (task.DelegatorId is { } d) recipients.Add(d); }
        else foreach (var a in task.Assignees.Where(a => a.RemovedAt == null && a.EmployeeId is not null)) recipients.Add(a.EmployeeId!.Value);
        if (me is { } self) recipients.Remove(self);
        if (recipients.Count > 0)
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = $"comment:{comment.Id:N}",
                Category = NotificationCategory.Comment,
                Task = task,
                RecipientIds = recipients,
                Headline = $"has a new comment from {actor.Name}.",
                Message = NotificationService.Shorten(comment.Body, 400),
                Channels = NotifyChannels.InApp,
            }, ct);

        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return comment;
    }

    /// <summary>Detects promises such as "we will complete this by Friday" and records them as commitments.</summary>
    internal async Task<List<Commitment>> ExtractCommitmentsAsync(WorkTask task, string text, Guid? personId, CommunicationSource source,
        Guid? messageId, Guid? commentId, DateTime sentAt, CancellationToken ct)
    {
        var cal = await calendars.GetDefaultAsync(ct);
        IReadOnlyList<ExtractedCommitment> found;
        try { found = await commitmentExtractor.ExtractAsync(text, sentAt, cal, ct); }
        catch (Exception) { return []; }   // AI failure must never block the user's action (spec §78)
        var created = new List<Commitment>();
        foreach (var c in found.Where(c => c.Confidence >= 0.5).Take(2))
            created.Add(await AddCommitmentInternalAsync(task, personId, c.Date, c.Time, c.Text, c.What, source, messageId, commentId, c.Confidence, sentAt, ct));
        return created;
    }

    public async Task<Commitment> AddCommitmentAsync(Guid id, CommitmentInput r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        if (!await CanManageAsync(task, ct) && !(actor.EmployeeId is { } me && await IsResponsibleAsync(task, me, ct)))
            throw new ForbiddenException("You cannot record commitments on this task.");
        var c = await AddCommitmentInternalAsync(task, r.EmployeeId ?? actor.EmployeeId, r.CommitmentDate, null, r.Text, null, CommunicationSource.Manual, null, null, 1.0, clock.UtcNow, ct);
        await monitoring.RefreshAsync(task, ct);
        await SaveAsync(ct);
        return c;
    }

    internal async Task<Commitment> AddCommitmentInternalAsync(WorkTask task, Guid? personId, DateOnly date, TimeOnly? time, string text, string? what,
        CommunicationSource source, Guid? messageId, Guid? commentId, double confidence, DateTime at, CancellationToken ct)
    {
        // A newer promise by the same person supersedes the older open one.
        var older = await db.Commitments.Where(c => c.TaskId == task.Id && c.EmployeeId == personId && c.Status == CommitmentStatus.Open).ToListAsync(ct);
        older.AddRange(db.Commitments.Local.Where(c => c.TaskId == task.Id && c.EmployeeId == personId && c.Status == CommitmentStatus.Open && !older.Contains(c)));
        foreach (var o in older.Where(o => o.CommitmentDate != date))
        {
            o.Status = CommitmentStatus.Superseded;
            o.ResolvedAt = at;
            o.ResolutionNote = $"Superseded by a new commitment for {date:d MMM}";
        }
        var same = older.FirstOrDefault(o => o.CommitmentDate == date);
        if (same is not null) return same;

        var commitment = new Commitment
        {
            TaskId = task.Id,
            EmployeeId = personId,
            CommittedToId = task.DelegatorId ?? task.OriginalDelegatorId,
            CommitmentDate = date,
            CommitmentTime = time,
            What = what,
            OriginalText = NotificationService.Shorten(text, 2000),
            Source = source,
            SourceMessageId = messageId,
            SourceCommentId = commentId,
            Confidence = confidence,
            CreatedAt = at,
        };
        db.Commitments.Add(commitment);
        audit.Record("CommitmentCreated", nameof(Commitment), commitment.Id, task.Id, "CommitmentDate", null, date.ToString("yyyy-MM-dd"), NotificationService.Shorten(text, 300));
        return commitment;
    }

    // ═══════════════════════════════ Completion & approval (spec §30–§32) ═══════════════════════════════

    public async Task<WorkTask> CompleteAsync(Guid id, CompleteRequest r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        var me = actor.EmployeeId;
        var responsible = me is { } m && await IsResponsibleAsync(task, m, ct);
        if (!responsible && !await CanManageAsync(task, ct) && !actor.IsSystem) throw new ForbiddenException("Only the responsible people can mark this task complete.");
        if (task.IsClosed || TaskStatusRules.IsAwaitingConfirmation(task.Status))
            throw new ConflictException("already_completed", "Completion has already been submitted.");
        var openSubtasks = await db.Tasks.CountAsync(t => t.ParentTaskId == task.Id && t.Status != WorkStatus.Completed && t.Status != WorkStatus.Cancelled, ct);
        if (openSubtasks > 0 && !r.ForceWithOpenSubtasks)
            throw new ValidationException("open_subtasks", $"{openSubtasks} subtask(s) are still open. Complete them first or confirm to complete anyway.");

        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        task.CompletionRound++;
        var submission = new CompletionSubmission
        {
            TaskId = task.Id,
            Round = task.CompletionRound,
            SubmittedById = me ?? task.PrimaryAssigneeId ?? Guid.Empty,
            SubmittedAt = now,
            CompletionDate = r.CompletionDate ?? cal.Today(now),
            Note = r.Note,
            Source = actor.Source,
        };
        foreach (var e in r.Evidence ?? [])
            submission.Evidence.Add(new CompletionEvidence { TaskId = task.Id, SubmissionId = submission.Id, Kind = e.Kind, Title = e.Title, Url = e.Url, Reference = e.Reference, AttachmentId = e.AttachmentId, AddedAt = now, AddedById = me });
        db.CompletionSubmissions.Add(submission);
        if (r.Evidence is { Count: > 0 })
        {
            var attachmentIds = r.Evidence.Where(e => e.AttachmentId is not null).Select(e => e.AttachmentId!.Value).ToList();
            await db.Attachments.Where(a => attachmentIds.Contains(a.Id)).ForEachAsync(a => { a.TaskId = task.Id; a.IsEvidence = true; }, ct);
        }

        task.ProgressPercent = 100;
        ChangeStatusInternal(task, WorkStatus.CompletedByAssignee, r.Note ?? "Work completed", now);
        audit.Record("CompletionSubmitted", nameof(WorkTask), task.Id, task.Id, "CompletionRound", null, task.CompletionRound.ToString(), r.Note);
        if (responsible) RecordAssigneeActivity(task, now, "completion"); else task.LastActivityAt = now;
        task.IsBlocked = false;
        task.BlockedSince = null;

        var approvers = await ResolveApproversAsync(task, me, ct);
        if (task.RequiresApproval && approvers.Count > 0)
        {
            var stage = 1;
            foreach (var (role, approverId) in approvers)
                db.TaskApprovals.Add(new TaskApproval { TaskId = task.Id, Round = task.CompletionRound, Stage = stage++, ApproverRole = role, ApproverId = approverId, RequestedAt = now });
            ChangeStatusInternal(task, WorkStatus.CompletionAwaitingApproval, "Awaiting completion confirmation", now);
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = $"approval:{task.Id:N}:{task.CompletionRound}:1",
                Category = NotificationCategory.ApprovalRequest,
                Task = task,
                RecipientIds = [approvers[0].ApproverId],
                Headline = "was marked complete and needs your confirmation (Confirm, Reopen or Request More Work).",
                Message = r.Note,
                Channels = NotifyChannels.InApp | NotifyChannels.Email | NotifyChannels.Teams,
            }, ct);
        }
        else
        {
            await FinaliseCompletionAsync(task, submission, now, approvers.Count == 0 && task.RequiresApproval ? "No approver available" : "No approval required", ct);
        }

        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    private async Task FinaliseCompletionAsync(WorkTask task, CompletionSubmission submission, DateTime now, string reason, CancellationToken ct)
    {
        ChangeStatusInternal(task, WorkStatus.Completed, reason, now);
        task.CompletedAt = submission.SubmittedAt;
        task.ClosedAt = now;
        task.NextFollowUpAt = null;
        task.NoResponseSince = null;
        task.RemindersSinceResponse = 0;
        audit.Record("Completed", nameof(WorkTask), task.Id, task.Id, details: reason);

        var cal = await calendars.GetDefaultAsync(ct);
        var completedOn = submission.CompletionDate;
        var open = await db.Commitments.Where(c => c.TaskId == task.Id && c.Status == CommitmentStatus.Open).ToListAsync(ct);
        foreach (var c in open)
        {
            c.Status = completedOn <= c.CommitmentDate ? CommitmentStatus.Met : CommitmentStatus.Missed;
            c.ResolvedAt = now;
            c.ResolutionNote = c.Status == CommitmentStatus.Met ? "Task completed on time" : $"Completed on {completedOn:d MMM}, after the committed date";
        }
        _ = cal;

        if (task.DelegatorId is { } d && d != actor.EmployeeId)
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = $"completed:{task.Id:N}:{task.CompletionRound}",
                Category = NotificationCategory.StatusChange,
                Task = task,
                RecipientIds = [d],
                Headline = "has been completed.",
                Message = submission.Note,
                Channels = task.Criticality >= Criticality.Important ? NotifyChannels.InApp | NotifyChannels.Email : NotifyChannels.InApp,
            }, ct);

        // Completing a task may unblock its dependants.
        var dependants = await db.TaskDependencies.Where(x => x.DependsOnTaskId == task.Id).Select(x => x.TaskId).ToListAsync(ct);
        foreach (var depId in dependants.Distinct())
        {
            var dep = await db.Tasks.FirstOrDefaultAsync(x => x.Id == depId, ct);
            if (dep is not null) await monitoring.RefreshAsync(dep, ct);
        }
    }

    /// <summary>Approval flow (spec §31): team leader / delegator / department head / CEO; never the submitter.</summary>
    private async Task<List<(ApproverRole Role, Guid ApproverId)>> ResolveApproversAsync(WorkTask task, Guid? submitter, CancellationToken ct)
    {
        IReadOnlyList<ApproverRole> flow = ApprovalRules.DefaultFlow(task.Criticality);
        if (task.TemplateId is { } tid && await db.TaskTemplates.Where(t => t.Id == tid).Select(t => t.ApprovalFlow).FirstOrDefaultAsync(ct) is { Count: > 0 } custom)
            flow = custom;
        var result = new List<(ApproverRole, Guid)>();
        foreach (var role in flow)
        {
            Guid? id = role switch
            {
                ApproverRole.TeamLeader => task.TeamId is { } team ? await db.Teams.Where(t => t.Id == team).Select(t => t.LeaderId).FirstOrDefaultAsync(ct) : null,
                ApproverRole.Delegator => task.DelegatorId ?? task.OriginalDelegatorId,
                ApproverRole.DepartmentHead => task.DepartmentId is { } dep ? await db.Departments.Where(d => d.Id == dep).Select(d => d.HeadId).FirstOrDefaultAsync(ct) : null,
                ApproverRole.AccountableManager => task.AccountableManagerId,
                _ => await CeoIdAsync(task, ct),
            };
            if (id is { } v && v != submitter && result.All(x => x.Item2 != v)) result.Add((role, v));
        }
        return result;
    }

    internal async Task<Guid?> CeoIdAsync(WorkTask? task, CancellationToken ct)
    {
        if (task?.OriginalDelegatorId is { } od && await db.UserAccounts.AnyAsync(a => a.EmployeeId == od && (a.Roles & AppRole.Executive) == AppRole.Executive, ct))
            return od;
        return await db.UserAccounts.Where(a => (a.Roles & AppRole.Executive) == AppRole.Executive && a.IsEnabled).OrderBy(a => a.CreatedAt).Select(a => (Guid?)a.EmployeeId).FirstOrDefaultAsync(ct);
    }

    public async Task<WorkTask> DecideApprovalAsync(Guid id, ApprovalRequest r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        var me = actor.EmployeeId ?? throw new ForbiddenException("Sign in to approve.");
        var now = clock.UtcNow;
        var pending = await db.TaskApprovals.Where(a => a.TaskId == task.Id && a.Round == task.CompletionRound && a.Decision == ApprovalDecision.Pending)
            .OrderBy(a => a.Stage).ToListAsync(ct);
        var current = pending.FirstOrDefault();
        var isExec = actor.Viewer.IsExecutive;
        if (task.Status == WorkStatus.CompletedByAssignee && current is null)
        {
            // Completion awaiting the delegator's confirmation without a formal flow.
            if (!await CanManageAsync(task, ct)) throw new ForbiddenException("Only the delegator or a manager can confirm completion.");
        }
        else
        {
            if (current is null) throw new ConflictException("no_pending_approval", "There is no completion awaiting confirmation.");
            if (current.ApproverId != me && !isExec) throw new ForbiddenException("This approval is assigned to someone else.");
        }
        var submission = await db.CompletionSubmissions.Where(s => s.TaskId == task.Id && s.Round == task.CompletionRound).FirstOrDefaultAsync(ct)
            ?? throw new ConflictException("no_submission", "No completion submission found.");

        if (r.Action == ApprovalAction.Confirm)
        {
            if (current is not null)
            {
                current.Decision = ApprovalDecision.Approved;
                current.Comment = r.Comment;
                current.DecidedAt = now;
                if (current.ApproverId != me) current.Comment = $"Approved by {actor.Name} on behalf of the approver. {r.Comment}".Trim();
            }
            audit.Record("Approval", nameof(TaskApproval), current?.Id, task.Id, "Decision", null, "Confirmed", r.Comment);
            var next = pending.Skip(1).FirstOrDefault();
            if (next is not null)
            {
                await notifications.EnqueueAsync(new NoticeRequest
                {
                    Key = $"approval:{task.Id:N}:{task.CompletionRound}:{next.Stage}",
                    Category = NotificationCategory.ApprovalRequest,
                    Task = task,
                    RecipientIds = [next.ApproverId],
                    Headline = "was confirmed at the previous stage and now needs your confirmation.",
                    Channels = NotifyChannels.InApp | NotifyChannels.Email | NotifyChannels.Teams,
                }, ct);
            }
            else
            {
                await FinaliseCompletionAsync(task, submission, now, $"Completion confirmed by {actor.Name}", ct);
                if (submission.SubmittedById != me && submission.SubmittedById != Guid.Empty)
                    await notifications.EnqueueAsync(new NoticeRequest
                    {
                        Key = $"approved:{task.Id:N}:{task.CompletionRound}",
                        Category = NotificationCategory.ApprovalDecision,
                        Task = task,
                        RecipientIds = [submission.SubmittedById],
                        Headline = $"— completion confirmed by {actor.Name}.",
                        Message = r.Comment,
                        Channels = NotifyChannels.InApp | NotifyChannels.Email,
                    }, ct);
            }
        }
        else
        {
            if (current is not null)
            {
                current.Decision = r.Action == ApprovalAction.Reopen ? ApprovalDecision.Reopened : ApprovalDecision.MoreWorkRequested;
                current.Comment = r.Comment;
                current.DecidedAt = now;
            }
            foreach (var other in pending.Skip(1)) other.Decision = ApprovalDecision.Superseded;
            var label = r.Action == ApprovalAction.Reopen ? "Reopened" : "More work requested";
            ChangeStatusInternal(task, WorkStatus.InProgress, $"{label}: {r.Comment}", now);
            task.ProgressPercent = Math.Min(task.ProgressPercent, 90);
            audit.Record("Reopened", nameof(WorkTask), task.Id, task.Id, "Decision", null, label, r.Comment);
            var cal = await calendars.GetDefaultAsync(ct);
            task.NextFollowUpAt = FollowUpScheduler.Next(task.FollowUp, now, task.StartDate ?? cal.Today(now), cal);
            if (submission.SubmittedById != Guid.Empty)
                await notifications.EnqueueAsync(new NoticeRequest
                {
                    Key = $"reopened:{task.Id:N}:{task.CompletionRound}",
                    Category = NotificationCategory.ApprovalDecision,
                    Task = task,
                    RecipientIds = [submission.SubmittedById],
                    Headline = $"was {(r.Action == ApprovalAction.Reopen ? "reopened" : "returned for more work")} by {actor.Name}.",
                    Message = r.Comment,
                    Channels = NotifyChannels.InApp | NotifyChannels.Email | NotifyChannels.Teams,
                }, ct);
        }
        if (!string.IsNullOrWhiteSpace(r.Comment))
            db.TaskComments.Add(new TaskComment { TaskId = task.Id, AuthorId = me, Body = $"[{Text.Humanize(r.Action)}] {r.Comment}", CreatedAt = now });

        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        UiEvents.Publish(db, clock, UiEvents.AttentionChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    public async Task<WorkTask> ReopenAsync(Guid id, string? reason, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        await RequireManageAsync(task, ct);
        if (!task.IsClosed) throw new ConflictException("not_closed", "Only completed or cancelled tasks can be reopened.");
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        ChangeStatusInternal(task, WorkStatus.InProgress, $"Reopened: {reason}", now);
        task.ClosedAt = null;
        task.CompletedAt = null;
        task.ProgressPercent = Math.Min(task.ProgressPercent, 90);
        task.NextFollowUpAt = FollowUpScheduler.Next(task.FollowUp, now, cal.Today(now), cal);
        audit.Record("Reopened", nameof(WorkTask), task.Id, task.Id, details: reason);
        if (task.PrimaryAssigneeId is { } p)
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = $"reopen:{task.Id:N}:{now.Ticks}",
                Category = NotificationCategory.StatusChange,
                Task = task,
                RecipientIds = [p],
                Headline = $"was reopened by {actor.Name}.",
                Message = reason,
            }, ct);
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    // ═══════════════════════════════ Extensions (spec §44) ═══════════════════════════════

    public async Task<ExtensionRequest> RequestExtensionAsync(Guid id, ExtensionRequestInput r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        var me = actor.EmployeeId ?? throw new ForbiddenException("Sign in to request an extension.");
        if (!await IsResponsibleAsync(task, me, ct)) throw new ForbiddenException("Only the responsible people can request an extension.");
        if (string.IsNullOrWhiteSpace(r.Reason)) throw new ValidationException("reason_required", "Give a reason for the extension.");
        var now = clock.UtcNow;
        var ext = await CreateExtensionAsync(task, me, r.ProposedDueDate, r.Reason, CommunicationSource.Manual, now, ct);
        RecordAssigneeActivity(task, now, "extension request");
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return ext;
    }

    internal async Task<ExtensionRequest> CreateExtensionAsync(WorkTask task, Guid requesterId, DateOnly proposed, string reason, CommunicationSource source, DateTime now, CancellationToken ct)
    {
        var pending = await db.ExtensionRequests.FirstOrDefaultAsync(x => x.TaskId == task.Id && x.Status == ExtensionStatus.Pending, ct);
        if (pending is not null)
        {
            pending.ProposedDueDate = proposed;
            pending.Reason = reason;
            pending.RequestedAt = now;
            return pending;
        }
        var approver = task.DelegatorId ?? task.AccountableManagerId ?? await CeoIdAsync(task, ct);
        var ext = new ExtensionRequest
        {
            TaskId = task.Id,
            RequestedById = requesterId,
            OriginalDueDate = task.OriginalDueDate ?? task.DueDate,
            CurrentDueDate = task.DueDate,
            ProposedDueDate = proposed,
            Reason = reason,
            ApproverId = approver,
            RequestedAt = now,
            Source = source,
        };
        db.ExtensionRequests.Add(ext);
        audit.Record("ExtensionRequested", nameof(ExtensionRequest), ext.Id, task.Id, "DueDate", task.DueDate?.ToString("yyyy-MM-dd"), proposed.ToString("yyyy-MM-dd"), reason);
        if (approver is { } a)
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = $"ext:{ext.Id:N}",
                Category = NotificationCategory.ExtensionRequest,
                Task = task,
                RecipientIds = [a],
                Headline = $"— deadline extension to {proposed:ddd d MMM} requested.",
                Message = reason,
                Channels = NotifyChannels.InApp | NotifyChannels.Email,
            }, ct);
        return ext;
    }

    public async Task<WorkTask> DecideExtensionAsync(Guid extensionId, ExtensionDecisionRequest r, CancellationToken ct)
    {
        var ext = await db.ExtensionRequests.FirstOrDefaultAsync(x => x.Id == extensionId, ct) ?? throw new NotFoundException("Extension request");
        var task = await LoadAsync(ext.TaskId, ct);
        if (ext.Status != ExtensionStatus.Pending) throw new ConflictException("already_decided", "This request has already been decided.");
        if (ext.ApproverId != actor.EmployeeId && !actor.Viewer.IsExecutive && !await CanManageAsync(task, ct))
            throw new ForbiddenException("Only the approver can decide this request.");
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        ext.Status = r.Approve ? ExtensionStatus.Approved : ExtensionStatus.Rejected;
        ext.DecisionNote = r.Note;
        ext.DecidedAt = now;
        ext.DecidedById = actor.EmployeeId;
        if (r.Approve)
        {
            var newDate = r.OverrideDate ?? ext.ProposedDueDate;
            ApplyDueDateChange(task, newDate, task.DueTime, $"Extension approved: {ext.Reason}", cal);
            if (task.Acknowledgement == AcknowledgementState.DeadlineProposed)
            {
                task.Acknowledgement = AcknowledgementState.Accepted;
                if (task.Status == WorkStatus.AcknowledgementPending) ChangeStatusInternal(task, WorkStatus.Accepted, "Accepted with the new deadline", now);
            }
        }
        audit.Record("ExtensionDecided", nameof(ExtensionRequest), ext.Id, task.Id, "Decision", null, ext.Status.ToString(), r.Note);
        await notifications.EnqueueAsync(new NoticeRequest
        {
            Key = $"extdecision:{ext.Id:N}",
            Category = NotificationCategory.ExtensionDecision,
            Task = task,
            RecipientIds = [ext.RequestedById],
            Headline = r.Approve ? $"— extension approved; new due date {task.DueDate:ddd d MMM}." : "— extension request was not approved.",
            Message = r.Note,
            Channels = NotifyChannels.InApp | NotifyChannels.Email,
        }, ct);
        task.LastActivityAt = now;
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        UiEvents.Publish(db, clock, UiEvents.AttentionChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    // ═══════════════════════════════ Snooze / pause follow-up (spec §45) ═══════════════════════════════

    public async Task<WorkTask> SnoozeAsync(Guid id, SnoozeRequest r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        var me = actor.EmployeeId;
        if (!await CanManageAsync(task, ct) && !(me is { } m && await IsResponsibleAsync(task, m, ct))) throw new ForbiddenException("You cannot pause follow-up on this task.");
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        if (r.ResumeOn <= cal.Today(now)) throw new ValidationException("resume_date", "The resume date must be in the future.");
        if (string.IsNullOrWhiteSpace(r.Note) && r.Reason == FollowUpPauseReason.Other) throw new ValidationException("reason_required", "Explain why follow-up is paused.");
        task.FollowUpPausedUntil = r.ResumeOn;
        task.FollowUpPauseReason = r.Reason;
        task.FollowUpPauseNote = r.Note;
        task.NextFollowUpAt = task.FollowUp.IsEnabled ? FollowUpScheduler.Next(task.FollowUp, cal.ToUtc(r.ResumeOn, TimeOnly.MinValue), r.ResumeOn, cal) : null;
        if (r.Reason is FollowUpPauseReason.Customer or FollowUpPauseReason.Vendor or FollowUpPauseReason.GovernmentDepartment or FollowUpPauseReason.ExternalInformation
            && task.Status is not WorkStatus.WaitingForExternalParty && task.IsOpen)
            ChangeStatusInternal(task, WorkStatus.WaitingForExternalParty, $"Waiting for {Text.Humanize(r.Reason).ToLowerInvariant()}: {r.Note}", now);
        audit.Record("FollowUpPaused", nameof(WorkTask), task.Id, task.Id, "PausedUntil", null, r.ResumeOn.ToString("yyyy-MM-dd"), $"{r.Reason}: {r.Note}");
        task.LastActivityAt = now;
        if (me is { } m2 && await IsResponsibleAsync(task, m2, ct)) RecordAssigneeActivity(task, now, "snooze");
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    public async Task<WorkTask> ResumeFollowUpAsync(Guid id, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        if (!await CanManageAsync(task, ct) && !(actor.EmployeeId is { } m && await IsResponsibleAsync(task, m, ct))) throw new ForbiddenException("You cannot resume follow-up on this task.");
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        task.FollowUpPausedUntil = null;
        task.FollowUpPauseReason = null;
        task.FollowUpPauseNote = null;
        task.NextFollowUpAt = FollowUpScheduler.Next(task.FollowUp, now, cal.Today(now), cal);
        audit.Record("FollowUpResumed", nameof(WorkTask), task.Id, task.Id);
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return task;
    }

    // ═══════════════════════════════ Dependencies (spec §16) ═══════════════════════════════

    public async Task<TaskDependency> AddDependencyAsync(Guid id, DependencyInput r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        if (!await CanManageAsync(task, ct) && !(actor.EmployeeId is { } m && await IsResponsibleAsync(task, m, ct))) throw new ForbiddenException("You cannot change dependencies.");
        var dep = await AddDependencyInternalAsync(task, r, clock.UtcNow, ct);
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return dep;
    }

    private async Task<TaskDependency> AddDependencyInternalAsync(WorkTask task, DependencyInput r, DateTime now, CancellationToken ct)
    {
        if (r.TaskId is { } other)
        {
            if (other == task.Id) throw new ValidationException("self_dependency", "A task cannot depend on itself.");
            if (!await db.Tasks.AnyAsync(t => t.Id == other, ct)) throw new NotFoundException("Dependency task");
            if (await CreatesCycleAsync(task.Id, other, ct)) throw new ValidationException("dependency_cycle", "That dependency would create a cycle.");
        }
        else if (string.IsNullOrWhiteSpace(r.ExternalDescription)) throw new ValidationException("dependency_invalid", "Choose a task or describe the external dependency.");

        var dep = new TaskDependency
        {
            TaskId = task.Id,
            DependsOnTaskId = r.TaskId,
            Type = r.TaskId is null ? DependencyType.External : r.Type,
            ExternalDescription = r.ExternalDescription,
            ExternalParty = r.ExternalParty,
            ExpectedBy = r.ExpectedBy,
            CreatedAt = now,
            CreatedById = actor.EmployeeId,
        };
        db.TaskDependencies.Add(dep);
        var label = r.TaskId is { } t ? await db.Tasks.Where(x => x.Id == t).Select(x => x.Number).FirstOrDefaultAsync(ct) : r.ExternalDescription;
        audit.Record("DependencyAdded", nameof(TaskDependency), dep.Id, task.Id, dep.Type.ToString(), null, label);
        return dep;
    }

    private async Task<bool> CreatesCycleAsync(Guid taskId, Guid dependsOn, CancellationToken ct)
    {
        var visited = new HashSet<Guid>();
        var stack = new Stack<Guid>([dependsOn]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == taskId) return true;
            if (!visited.Add(current)) continue;
            var next = await db.TaskDependencies.Where(d => d.TaskId == current && d.DependsOnTaskId != null).Select(d => d.DependsOnTaskId!.Value).ToListAsync(ct);
            foreach (var n in next) stack.Push(n);
        }
        return false;
    }

    public async Task RemoveDependencyAsync(Guid id, Guid dependencyId, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        await RequireManageAsync(task, ct);
        var dep = await db.TaskDependencies.FirstOrDefaultAsync(d => d.Id == dependencyId && d.TaskId == id, ct) ?? throw new NotFoundException("Dependency");
        db.TaskDependencies.Remove(dep);
        audit.Record("DependencyRemoved", nameof(TaskDependency), dep.Id, task.Id);
        await SaveAsync(ct);
        await monitoring.RefreshAsync(task, ct);
        await SaveAsync(ct);
    }

    public async Task ResolveDependencyAsync(Guid id, Guid dependencyId, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        if (!await CanManageAsync(task, ct) && !(actor.EmployeeId is { } m && await IsResponsibleAsync(task, m, ct))) throw new ForbiddenException("You cannot change dependencies.");
        var dep = await db.TaskDependencies.FirstOrDefaultAsync(d => d.Id == dependencyId && d.TaskId == id, ct) ?? throw new NotFoundException("Dependency");
        var now = clock.UtcNow;
        dep.IsResolved = true;
        dep.ResolvedAt = now;
        audit.Record("DependencyResolved", nameof(TaskDependency), dep.Id, task.Id, details: dep.ExternalDescription);
        if (task.Status is WorkStatus.WaitingForExternalParty or WorkStatus.Blocked && !await HasOpenBlockingDependenciesAsync(task, ct, excluding: dep.Id))
            ChangeStatusInternal(task, WorkStatus.InProgress, $"Dependency resolved: {dep.ExternalDescription}", now);
        task.LastActivityAt = now;
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
    }

    private async Task<bool> HasOpenBlockingDependenciesAsync(WorkTask task, CancellationToken ct, Guid? excluding = null) =>
        await db.TaskDependencies.AnyAsync(d => d.TaskId == task.Id && d.Id != excluding && !d.IsResolved
            && (d.DependsOnTaskId == null || (d.DependsOnTask!.Status != WorkStatus.Completed && d.DependsOnTask.Status != WorkStatus.Cancelled)), ct);

    // ═══════════════════════════════ Quick actions (spec §57) ═══════════════════════════════

    public async Task<int> SendReminderAsync(Guid id, ReminderRequest r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        if (!await CanManageAsync(task, ct)) throw new ForbiddenException("Only the delegator or a manager can send reminders.");
        if (task.IsClosed) throw new ConflictException("closed", "The task is closed.");
        var now = clock.UtcNow;
        var recipients = new HashSet<Guid>();
        if (task.PrimaryAssigneeId is { } p) recipients.Add(p);
        if (r.IncludeTeamLeader && task.TeamId is { } tid && await db.Teams.Where(t => t.Id == tid).Select(t => t.LeaderId).FirstOrDefaultAsync(ct) is { } tl) recipients.Add(tl);
        var channels = NotifyChannels.InApp | (r.Email ? NotifyChannels.Email : 0) | (r.Teams ? NotifyChannels.Teams : 0);
        var count = await notifications.EnqueueAsync(new NoticeRequest
        {
            Key = $"manual:{task.Id:N}:{now.Ticks}",
            Category = NotificationCategory.Reminder,
            Task = task,
            RecipientIds = recipients,
            Headline = r.RequestUpdate ? $"— {actor.Name} has requested an update." : $"— reminder from {actor.Name}.",
            Message = r.Message,
            Channels = channels,
            SupersedeKey = $"rem:{task.Id:N}",
            ExpiresIn = TimeSpan.FromHours(_o.ReminderExpiryHours),
        }, ct);
        task.LastReminderAt = now;
        task.RemindersSinceResponse++;
        task.NoResponseSince ??= now;
        audit.Record("ReminderSent", nameof(WorkTask), task.Id, task.Id, "Channels", null, channels.ToString(), r.Message);
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return count;
    }

    public async Task<TaskEscalation> EscalateAsync(Guid id, EscalateRequest r, CancellationToken ct)
    {
        var task = await LoadAsync(id, ct);
        if (!await CanManageAsync(task, ct) && !(actor.EmployeeId is { } m && await IsResponsibleAsync(task, m, ct))) throw new ForbiddenException("You cannot escalate this task.");
        var now = clock.UtcNow;
        var level = Math.Max(task.EscalationLevel + 1, r.Targets.HasFlag(EscalationTarget.Ceo) ? 5 : 2);
        var esc = new TaskEscalation
        {
            TaskId = task.Id,
            Level = level,
            Trigger = EscalationTrigger.Overdue,
            IdempotencyKey = $"manual:{task.Id:N}:{now.Ticks}",
            Targets = r.Targets,
            Reason = r.Note,
            TriggeredAt = now,
            TriggeredById = actor.EmployeeId,
            IsManual = true,
        };
        db.TaskEscalations.Add(esc);
        task.EscalationLevel = Math.Max(task.EscalationLevel, level);
        task.EscalationCount++;
        audit.Record("Escalated", nameof(TaskEscalation), esc.Id, task.Id, "Level", null, level.ToString(), r.Note);
        var recipients = await EscalationService.ResolveTargetsAsync(db, task, r.Targets, this, ct);
        await notifications.EnqueueAsync(new NoticeRequest
        {
            Key = $"esc:{esc.Id:N}",
            Category = r.Targets.HasFlag(EscalationTarget.Ceo) ? NotificationCategory.CeoAlert : NotificationCategory.Escalation,
            Task = task,
            RecipientIds = recipients.Where(x => x != actor.EmployeeId).ToList(),
            Headline = $"has been escalated by {actor.Name}.",
            Message = r.Note,
            Urgent = true,
            Channels = NotifyChannels.InApp | NotifyChannels.Email | NotifyChannels.Teams,
        }, ct);
        if (r.Targets.HasFlag(EscalationTarget.Ceo))
            monitoring.UpsertAttention(AttentionType.EscalatedToCeo, $"escalation:{esc.Id:N}", task, $"Escalated to you: {task.Title}", r.Note, task.AttentionScore + 10);
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await SaveAsync(ct);
        return esc;
    }

    // ═══════════════════════════════ AI suggestions & message links (spec §27, §33) ═══════════════════════════════

    public async Task<WorkTask> DecideSuggestionAsync(Guid suggestionId, bool accept, CancellationToken ct)
    {
        var s = await db.TaskSuggestions.FirstOrDefaultAsync(x => x.Id == suggestionId, ct) ?? throw new NotFoundException("Suggestion");
        var task = await LoadAsync(s.TaskId, ct);
        var manage = await CanManageAsync(task, ct);
        var responsible = actor.EmployeeId is { } me && await IsResponsibleAsync(task, me, ct);
        if (!manage && !responsible) throw new ForbiddenException("You cannot act on this suggestion.");
        if (s.Status != SuggestionStatus.Pending) throw new ConflictException("already_decided", "This suggestion has already been handled.");
        var now = clock.UtcNow;
        s.Status = accept ? SuggestionStatus.Accepted : SuggestionStatus.Dismissed;
        s.DecidedAt = now;
        s.DecidedById = actor.EmployeeId;
        audit.Record("SuggestionDecided", nameof(TaskSuggestion), s.Id, task.Id, s.Kind.ToString(), null, s.Status.ToString(), s.Explanation);
        await db.SaveChangesAsync(ct);
        if (!accept) return task;

        switch (s.Kind)
        {
            case SuggestionKind.MarkCompletedByAssignee:
                return await CompleteAsync(task.Id, new CompleteRequest(s.SuggestedDate, $"Completion confirmed from communication: {s.Detail}", null, true), ct);
            case SuggestionKind.ChangeStatus when s.SuggestedStatus is { } st && TaskStatusRules.CanTransition(task.Status, st):
                return await ChangeStatusAsync(task.Id, new StatusChangeRequest(st, s.Detail), ct);
            case SuggestionKind.ChangeDueDate when s.SuggestedDate is { } d:
                if (!manage) throw new ForbiddenException("Only the delegator can change the due date.");
                return await UpdateAsync(task.Id, new UpdateTaskRequest { DueDate = d, ChangeReason = s.Detail ?? "Accepted AI suggestion" }, ct);
            case SuggestionKind.RecordBlocker:
                return await ChangeStatusAsync(task.Id, new StatusChangeRequest(WorkStatus.Blocked, s.Detail), ct);
            default:
                return task;
        }
    }

    // ═══════════════════════════════ Helpers ═══════════════════════════════

    public async Task<WorkTask> LoadAsync(Guid id, CancellationToken ct)
    {
        var task = await db.Tasks.Include(t => t.Assignees).Include(t => t.Followers).Include(t => t.Tags)
            .FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Task");
        await EnsureVisibleAsync(task, ct);
        return task;
    }

    private async Task EnsureVisibleAsync(WorkTask task, CancellationToken ct)
    {
        if (actor.IsSystem || actor.Viewer.IsExecutive) return;
        if (!await db.Tasks.Where(t => t.Id == task.Id).VisibleTo(db, actor.Viewer).AnyAsync(ct)) throw new NotFoundException("Task");
    }

    public async Task<bool> CanManageAsync(WorkTask task, CancellationToken ct)
    {
        if (actor.IsSystem) return true;
        var teamIds = task.Assignees.Where(a => a.TeamId != null && a.RemovedAt == null).Select(a => a.TeamId!.Value).ToList();
        if (task.TeamId is { } t) teamIds.Add(t);
        if (TaskAccess.CanManage(actor.Viewer, task.DelegatorId, task.OriginalDelegatorId, task.CreatedById, task.AccountableManagerId, task.DepartmentId, teamIds))
            return true;
        // Delegating a subtask makes you a manager of the subtask; managers of the parent manage subtasks too.
        if (task.ParentTaskId is { } parentId)
        {
            var parent = await db.Tasks.Include(x => x.Assignees).FirstOrDefaultAsync(x => x.Id == parentId, ct);
            if (parent is not null && (parent.PrimaryAssigneeId == actor.EmployeeId || await CanManageAsync(parent, ct))) return true;
        }
        return false;
    }

    private async Task RequireManageAsync(WorkTask task, CancellationToken ct)
    {
        if (!await CanManageAsync(task, ct)) throw new ForbiddenException("Only the delegator, the accountable manager or a manager of this team can do that.");
    }

    /// <summary>People whose activity counts as a response: assignees, the team leader, and members of assigned teams.</summary>
    public async Task<HashSet<Guid>> ResponsibleIdsAsync(WorkTask task, CancellationToken ct)
    {
        var ids = task.Assignees.Where(a => a.RemovedAt == null && a.EmployeeId is not null).Select(a => a.EmployeeId!.Value).ToHashSet();
        if (task.PrimaryAssigneeId is { } p) ids.Add(p);
        var teamIds = task.Assignees.Where(a => a.RemovedAt == null && a.TeamId is not null && a.EmployeeId is null).Select(a => a.TeamId!.Value).ToList();
        if (teamIds.Count > 0)
            foreach (var m in await db.TeamMembers.Where(m => teamIds.Contains(m.TeamId)).Select(m => m.EmployeeId).ToListAsync(ct)) ids.Add(m);
        return ids;
    }

    public async Task<bool> IsResponsibleAsync(WorkTask task, Guid employeeId, CancellationToken ct) =>
        (await ResponsibleIdsAsync(task, ct)).Contains(employeeId);

    /// <summary>A response from the responsible side clears no-response tracking (spec §35).</summary>
    public static void RecordAssigneeActivity(WorkTask task, DateTime now, string what)
    {
        task.LastActivityAt = now;
        task.LastAssigneeActivityAt = now;
        task.NoResponseSince = null;
        task.RemindersSinceResponse = 0;
        _ = what;
    }

    private void AddSystemComment(WorkTask task, string body, DateTime now) =>
        db.TaskComments.Add(new TaskComment { TaskId = task.Id, Body = body, CreatedAt = now, IsSystem = true, AuthorId = actor.EmployeeId });

    private async Task CloseCommitmentsAsync(WorkTask task, CommitmentStatus status, string note, DateTime now, CancellationToken ct)
    {
        foreach (var c in await db.Commitments.Where(c => c.TaskId == task.Id && c.Status == CommitmentStatus.Open).ToListAsync(ct))
        {
            c.Status = status;
            c.ResolvedAt = now;
            c.ResolutionNote = note;
        }
    }

    private async Task ApplyTagsAsync(WorkTask task, IEnumerable<string> names, CancellationToken ct, bool replace = false)
    {
        var wanted = names.Select(n => n.Trim()).Where(n => n.Length > 0).Select(n => n.Length > 60 ? n[..60] : n).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (replace) task.Tags.RemoveAll(tt => true);
        foreach (var name in wanted)
        {
            var tag = db.Tags.Local.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? await db.Tags.FirstOrDefaultAsync(t => t.Name == name, ct);
            if (tag is null) { tag = new Tag { Name = name }; db.Tags.Add(tag); }
            if (task.Tags.All(x => x.TagId != tag.Id)) task.Tags.Add(new TaskTag { TaskId = task.Id, TagId = tag.Id, Tag = tag });
        }
    }

    /// <summary>Allocates TASK-yyyy-nnnnn atomically (increment-and-read in one statement).</summary>
    private async Task<(int Year, int Sequence, string Number)> AllocateNumberAsync(int year, CancellationToken ct)
    {
        var key = $"TASK-{year}";
        int next;
        if (db.IsSqlite)
        {
            await db.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO NumberSequences (\"Key\", NextValue) VALUES ({0}, 1)", [key], ct);
            next = (await db.Database.SqlQueryRaw<int>("UPDATE NumberSequences SET NextValue = NextValue + 1 WHERE \"Key\" = {0} RETURNING NextValue AS \"Value\"", key).ToListAsync(ct)).Single();
        }
        else
        {
            try { await db.Database.ExecuteSqlRawAsync("IF NOT EXISTS (SELECT 1 FROM NumberSequences WHERE [Key] = {0}) INSERT INTO NumberSequences ([Key], NextValue) VALUES ({0}, 1)", [key], ct); }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627) { }
            next = (await db.Database.SqlQueryRaw<int>("UPDATE NumberSequences SET NextValue = NextValue + 1 OUTPUT INSERTED.NextValue AS [Value] WHERE [Key] = {0}", key).ToListAsync(ct)).Single();
        }
        var seq = next - 1;
        return (year, seq, TaskNumbers.Format(year, seq));
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("concurrent_update", "The task was changed by someone else. Reload and try again."); }
    }
}
