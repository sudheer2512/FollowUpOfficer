using System.Globalization;
using FollowUp.Core.Contracts;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Core.Services;

public sealed partial class TaskQueryService
{
    private const int MaxSubtaskDepth = 6;

    /// <summary>Audit actions shown on the timeline (status, comments, etc. come from their own tables).</summary>
    private static readonly string[] TimelineAuditActions =
    [
        "TaskCreated", "TaskUpdated", "DeadlineChanged", "PriorityChanged", "CriticalityChanged", "VisibilityChanged", "Reassigned",
        "FollowUpChanged", "FollowUpPaused", "FollowUpResumed", "AssigneeAdded", "AssigneeRemoved", "DependencyAdded", "DependencyRemoved",
        "DependencyResolved", "ProgressUpdated", "Reopened", "AttachmentAdded", "LinkConfirmed",
    ];

    private static readonly HashSet<string> GuidFields = ["Project", "Category", "Department", "Team", "AccountableManager", "EscalationPolicy"];

    // ═══════════════════════════════ Task detail (spec §4–§8, §16, §27–§33, §43) ═══════════════════════════════

    public async Task<TaskDetail> GetDetailAsync(Guid id, CancellationToken ct)
    {
        var task = await tasks.LoadAsync(id, ct);               // 404 when missing or not visible
        var c = await ClockAsync(ct);
        var v = actor.Viewer;
        var me = actor.EmployeeId;
        var summary = (await projector.ProjectAsync(db.Tasks.AsNoTracking().Where(t => t.Id == id), c.Now, c.Calendar, c.SevereDays, ct)).Single();

        // ── Related rows (one query each; nothing per row) ──
        var comments = await db.TaskComments.AsNoTracking().Where(x => x.TaskId == id).OrderByDescending(x => x.CreatedAt).Take(300).ToListAsync(ct);
        var history = await db.TaskStatusHistory.AsNoTracking().Where(x => x.TaskId == id).OrderByDescending(x => x.ChangedAt).Take(300).ToListAsync(ct);
        var acks = await db.TaskAcknowledgements.AsNoTracking().Where(x => x.TaskId == id).OrderByDescending(x => x.RespondedAt).Take(100).ToListAsync(ct);
        var delegations = await db.TaskDelegations.AsNoTracking().Where(x => x.TaskId == id).OrderBy(x => x.Sequence).ToListAsync(ct);
        var approvals = await db.TaskApprovals.AsNoTracking().Where(x => x.TaskId == id).OrderByDescending(x => x.Round).ThenBy(x => x.Stage).ToListAsync(ct);
        var extensions = await db.ExtensionRequests.AsNoTracking().Where(x => x.TaskId == id).OrderByDescending(x => x.RequestedAt).ToListAsync(ct);
        var suggestions = await db.TaskSuggestions.AsNoTracking().Where(x => x.TaskId == id).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        var attachments = await db.Attachments.AsNoTracking().Where(x => x.TaskId == id).OrderByDescending(x => x.UploadedAt).ToListAsync(ct);
        var submissions = await db.CompletionSubmissions.AsNoTracking().Where(x => x.TaskId == id).OrderByDescending(x => x.Round).ToListAsync(ct);
        var evidence = await db.CompletionEvidence.AsNoTracking().Where(x => x.TaskId == id).OrderBy(x => x.AddedAt).ToListAsync(ct);
        var escalations = await db.TaskEscalations.AsNoTracking().Where(x => x.TaskId == id).OrderByDescending(x => x.TriggeredAt).ToListAsync(ct);
        var followUps = await db.TaskFollowUps.AsNoTracking().Where(x => x.TaskId == id).OrderByDescending(x => x.ProcessedAt).Take(200).ToListAsync(ct);
        var sent = await db.Notifications.AsNoTracking()
            .Where(n => n.TaskId == id && n.Channel != NotificationChannel.InApp && n.Status == NotificationStatus.Sent)
            .OrderByDescending(n => n.SentAt).Take(200)
            .Select(n => new { n.Id, n.Channel, n.Category, n.Subject, n.RecipientName, n.RecipientEmployeeId, n.SentAt, n.CreatedAt }).ToListAsync(ct);
        var auditRows = await db.AuditLogs.AsNoTracking().Where(a => a.TaskId == id && TimelineAuditActions.Contains(a.Action))
            .OrderByDescending(a => a.At).Take(300).ToListAsync(ct);
        var linkRows = await db.TaskMessageLinks.AsNoTracking().Where(l => l.TaskId == id && l.Status != LinkStatus.Rejected)
            .Select(l => new
            {
                l.Id, l.MessageId, l.Method, l.Confidence, l.Category, l.Explanation, l.Status, l.CreatedAt,
                l.Message!.Channel, l.Message.Subject, l.Message.BodyPreview,
                Body = l.Message.BodyText.Length > 400 ? l.Message.BodyText.Substring(0, 400) : l.Message.BodyText,
                l.Message.SenderName, l.Message.SenderEmployeeId, l.Message.SentAt, l.Message.WebUrl,
            }).ToListAsync(ct);

        // ── Dependencies in both directions ──
        var outgoing = await db.TaskDependencies.AsNoTracking().Where(d => d.TaskId == id)
            .Select(d => new DepRow(d.Id, d.Type, d.DependsOnTaskId, d.ExternalDescription, d.ExternalParty, d.ExpectedBy, d.IsResolved,
                d.DependsOnTask != null ? d.DependsOnTask.Number : null, d.DependsOnTask != null ? d.DependsOnTask.Title : null,
                d.DependsOnTask != null ? (WorkStatus?)d.DependsOnTask.Status : null)).ToListAsync(ct);
        var incoming = await db.TaskDependencies.AsNoTracking().Where(d => d.DependsOnTaskId == id)
            .Join(db.Tasks, d => d.TaskId, t => t.Id, (d, t) => new DepRow(d.Id, d.Type, t.Id, d.ExternalDescription, d.ExternalParty, d.ExpectedBy,
                d.IsResolved, t.Number, t.Title, (WorkStatus?)t.Status)).ToListAsync(ct);

        var relatedIds = outgoing.Select(d => d.OtherId).Concat(incoming.Select(d => d.OtherId)).Append(task.ParentTaskId)
            .Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        var visibleRelated = relatedIds.Count == 0 ? [] : (await db.Tasks.AsNoTracking().Where(t => relatedIds.Contains(t.Id)).VisibleTo(db, v)
            .Select(t => t.Id).ToListAsync(ct)).ToHashSet();

        // ── Subtask tree (visible descendants, depth-limited) ──
        var rootId = task.RootTaskId ?? task.Id;
        var maxDepth = task.Depth + MaxSubtaskDepth;
        var descendants = await db.Tasks.AsNoTracking().Where(t => t.RootTaskId == rootId && t.Depth > task.Depth && t.Depth <= maxDepth)
            .VisibleTo(db, v)
            .Select(t => new SubRow(t.Id, t.Number, t.Title, t.Status, t.ParentTaskId, t.PrimaryAssigneeId,
                t.PrimaryAssignee != null ? t.PrimaryAssignee.DisplayName : null, t.DueDate, t.DueTime, t.ProgressPercent, t.Health, t.Depth,
                t.IsBlocked, t.SortOrder))
            .ToListAsync(ct);

        // ── Names ──
        var personIds = new List<Guid?> { task.DelegatorId, task.OriginalDelegatorId, task.AccountableManagerId, task.CreatedById, task.PrimaryAssigneeId };
        personIds.AddRange(task.Assignees.Select(a => a.EmployeeId));
        personIds.AddRange(task.Followers.Select(f => (Guid?)f.EmployeeId));
        personIds.AddRange(comments.Select(x => x.AuthorId));
        personIds.AddRange(history.Select(x => x.ChangedById));
        personIds.AddRange(acks.Select(x => (Guid?)x.EmployeeId));
        personIds.AddRange(delegations.SelectMany(x => new[] { x.FromEmployeeId, x.ToEmployeeId }));
        personIds.AddRange(approvals.Select(x => (Guid?)x.ApproverId));
        personIds.AddRange(extensions.SelectMany(x => new[] { x.RequestedById, x.ApproverId, x.DecidedById }));
        personIds.AddRange(attachments.Select(x => x.UploadedById));
        personIds.AddRange(submissions.Select(x => (Guid?)x.SubmittedById));
        personIds.AddRange(escalations.Select(x => x.TriggeredById));
        personIds.AddRange(suggestions.Select(x => x.DecidedById));
        personIds.AddRange(linkRows.Select(x => x.SenderEmployeeId));
        personIds.AddRange(auditRows.Select(x => x.ActorId));
        var people = await PeopleAsync(personIds, ct);
        PersonRef? P(Guid? pid) => pid is { } x && people.TryGetValue(x, out var p) ? p : null;
        PersonRef PR(Guid pid, string fallback = "Unknown") => P(pid) ?? new PersonRef(pid, pid == Guid.Empty ? "Follow-up Officer" : fallback);

        var teamIds = task.Assignees.Select(a => a.TeamId).Append(task.TeamId).Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        var teamNames = await db.Teams.AsNoTracking().Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var project = task.ProjectId is { } pid ? await db.Projects.AsNoTracking().Where(p => p.Id == pid).Select(p => new NamedRef(p.Id, p.Name)).FirstOrDefaultAsync(ct) : null;
        var category = task.CategoryId is { } cid ? await db.TaskCategories.AsNoTracking().Where(p => p.Id == cid).Select(p => new NamedRef(p.Id, p.Name)).FirstOrDefaultAsync(ct) : null;
        var department = task.DepartmentId is { } did ? await db.Departments.AsNoTracking().Where(p => p.Id == did).Select(p => new NamedRef(p.Id, p.Name)).FirstOrDefaultAsync(ct) : null;
        var team = task.TeamId is { } tid && teamNames.TryGetValue(tid, out var tn) ? new NamedRef(tid, tn) : null;

        // ── Permissions ──
        var canManage = await tasks.CanManageAsync(task, ct);
        var responsible = me is { } m && await tasks.IsResponsibleAsync(task, m, ct);
        var currentApproval = approvals.Where(a => a.Round == task.CompletionRound && a.Decision == ApprovalDecision.Pending).OrderBy(a => a.Stage).FirstOrDefault();
        bool CanDecideApproval(TaskApproval a) => currentApproval is not null && a.Id == currentApproval.Id && (a.ApproverId == me || v.IsExecutive);
        bool CanDecideExtension(ExtensionRequest x) => x.Status == ExtensionStatus.Pending && (x.ApproverId == me || v.IsExecutive || canManage);
        var awaiting = TaskStatusRules.IsAwaitingConfirmation(task.Status);
        var permissions = new TaskPermissions(
            CanView: true,
            CanManage: canManage,
            CanUpdate: task.IsOpen && (canManage || responsible),
            CanComplete: task.IsOpen && !awaiting && (responsible || canManage),
            CanAcknowledge: responsible && task.IsOpen && task.Acknowledgement is AcknowledgementState.Pending or AcknowledgementState.ClarificationRequested
                or AcknowledgementState.DeadlineProposed,
            CanApprove: awaiting && (approvals.Any(CanDecideApproval) || (task.Status == WorkStatus.CompletedByAssignee && currentApproval is null && canManage)),
            CanComment: true,
            CanDecideExtension: extensions.Any(CanDecideExtension));

        // ── Sections ──
        var dependencies = outgoing.Select(d => ToDependency(d, "DependsOn", visibleRelated, IsBlockingDependency(d.Type, d.OtherId, d.OtherStatus, d.IsResolved)))
            .Concat(incoming.Select(d => ToDependency(d, "Blocks", visibleRelated, IsBlockingDependency(d.Type, task.Id, task.Status, d.IsResolved))))
            .ToList();

        var subtasks = BuildTree(task.Id, descendants, task.Depth, c);

        var commitmentDtos = await commitments.ProjectAsync(db.Commitments.AsNoTracking().Where(x => x.TaskId == id).OrderByDescending(x => x.CommitmentDate), ct);

        var evidenceBySubmission = evidence.Where(e => e.SubmissionId is not null).GroupBy(e => e.SubmissionId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var messages = linkRows
            .Where(l => l.Status == LinkStatus.Linked || canManage)
            .OrderByDescending(l => l.SentAt)
            .Select(l => new LinkedMessageDto(l.Id, l.MessageId, l.Channel, l.Subject, l.BodyPreview ?? NotificationService.Shorten(l.Body.ReplaceLineEndings(" "), 200),
                l.SenderName ?? P(l.SenderEmployeeId)?.Name, l.SentAt, l.Method, l.Confidence, l.Category, l.Explanation, l.Status, l.WebUrl))
            .ToList();

        string? recurringTitle = null, occurrenceKey = null;
        if (task.RecurringDefinitionId is { } rd)
            recurringTitle = await db.RecurringDefinitions.AsNoTracking().Where(d => d.Id == rd).Select(d => d.TitleTemplate).FirstOrDefaultAsync(ct);
        if (task.RecurringInstanceId is { } ri)
            occurrenceKey = await db.RecurringInstances.AsNoTracking().Where(i => i.Id == ri).Select(i => i.OccurrenceKey).FirstOrDefaultAsync(ct);
        Guid? meetingId = null;
        string? meetingTitle = null;
        if (task.MeetingActionItemId is { } mai)
        {
            var meeting = await db.MeetingActionItems.AsNoTracking().Where(x => x.Id == mai)
                .Join(db.Meetings, x => x.MeetingId, mt => mt.Id, (x, mt) => new { mt.Id, mt.Title }).FirstOrDefaultAsync(ct);
            meetingId = meeting?.Id;
            meetingTitle = meeting?.Title;
        }
        TaskListItem? parent = null;
        if (task.ParentTaskId is { } parentId && visibleRelated.Contains(parentId))
            parent = (await projector.ProjectAsync(db.Tasks.AsNoTracking().Where(t => t.Id == parentId), c.Now, c.Calendar, c.SevereDays, ct)).FirstOrDefault();

        // ── Timeline ──
        var guidNames = await GuidNamesAsync(auditRows.Where(a => a.Field is not null && GuidFields.Contains(a.Field)), people, ct);
        var timeline = new List<TimelineItem>();
        foreach (var x in comments)
            timeline.Add(x.IsSystem
                ? new TimelineItem(x.CreatedAt, "system", x.Body, null, P(x.AuthorId), "System", x.Id)
                : new TimelineItem(x.CreatedAt, "comment", $"{P(x.AuthorId)?.Name ?? "Someone"} commented", x.Body, P(x.AuthorId), "Comment", x.Id));
        foreach (var x in history)
            timeline.Add(new TimelineItem(x.ChangedAt, "status",
                x.FromStatus is null ? $"Created as {Text.Humanize(x.ToStatus)}" : $"Status changed from {Text.Humanize(x.FromStatus.Value)} to {Text.Humanize(x.ToStatus)}",
                x.Reason, P(x.ChangedById), x.Source.ToString(), x.Id,
                new() { ["from"] = x.FromStatus?.ToString() ?? "", ["to"] = x.ToStatus.ToString() }));
        foreach (var x in acks)
            timeline.Add(new TimelineItem(x.RespondedAt, "acknowledgement", AckTitle(P(x.EmployeeId)?.Name ?? "The assignee", x.Response, x.ProposedDueDate),
                x.Comment, P(x.EmployeeId), "User", x.Id, new() { ["response"] = x.Response.ToString() }));
        foreach (var x in delegations)
            timeline.Add(new TimelineItem(x.DelegatedAt, "delegation",
                x.FromEmployeeId is null ? $"Delegated to {PR(x.ToEmployeeId).Name}" : $"{PR(x.FromEmployeeId.Value).Name} delegated to {PR(x.ToEmployeeId).Name}",
                x.Note, P(x.FromEmployeeId), "User", x.Id, new() { ["sequence"] = x.Sequence.ToString(CultureInfo.InvariantCulture) }));
        foreach (var x in commitmentDtos)
        {
            timeline.Add(new TimelineItem(x.CreatedAt, "commitment",
                $"Commitment: {x.Person?.Name ?? "The team"} will finish by {x.CommitmentDate.ToString("ddd d MMM", CultureInfo.InvariantCulture)}",
                x.OriginalText, x.Person, x.Source.ToString(), x.Id, new() { ["status"] = x.Status.ToString(), ["date"] = x.CommitmentDate.ToString("yyyy-MM-dd") }));
            if (x.ResolvedAt is { } resolved && x.Status is CommitmentStatus.Met or CommitmentStatus.Missed)
                timeline.Add(new TimelineItem(resolved, "commitment",
                    x.Status == CommitmentStatus.Missed
                        ? $"Commitment for {x.CommitmentDate.ToString("ddd d MMM", CultureInfo.InvariantCulture)} was missed"
                        : $"Commitment for {x.CommitmentDate.ToString("ddd d MMM", CultureInfo.InvariantCulture)} was met",
                    null, x.Person, "System", x.Id, new() { ["status"] = x.Status.ToString() }));
        }
        foreach (var x in escalations)
            timeline.Add(new TimelineItem(x.TriggeredAt, "escalation", $"Escalated (level {x.Level}) to {Targets(x.Targets)}", x.Reason, P(x.TriggeredById),
                x.IsManual ? "User" : "System", x.Id, new() { ["trigger"] = x.Trigger.ToString(), ["level"] = x.Level.ToString(CultureInfo.InvariantCulture) }));
        foreach (var x in approvals)
        {
            timeline.Add(new TimelineItem(x.RequestedAt, "approval", $"Completion confirmation requested from {PR(x.ApproverId).Name} ({Text.Humanize(x.ApproverRole).ToLowerInvariant()})",
                null, null, "System", x.Id, new() { ["round"] = x.Round.ToString(CultureInfo.InvariantCulture), ["stage"] = x.Stage.ToString(CultureInfo.InvariantCulture) }));
            if (x.DecidedAt is { } decided && x.Decision != ApprovalDecision.Superseded)
                timeline.Add(new TimelineItem(decided, "approval", ApprovalTitle(PR(x.ApproverId).Name, x.Decision), x.Comment, P(x.ApproverId), "User", x.Id,
                    new() { ["decision"] = x.Decision.ToString() }));
        }
        foreach (var x in extensions)
        {
            timeline.Add(new TimelineItem(x.RequestedAt, "extension",
                $"{PR(x.RequestedById).Name} requested a deadline extension to {x.ProposedDueDate.ToString("ddd d MMM", CultureInfo.InvariantCulture)}",
                x.Reason, P(x.RequestedById), x.Source.ToString(), x.Id, new() { ["status"] = x.Status.ToString() }));
            if (x.DecidedAt is { } decided)
                timeline.Add(new TimelineItem(decided, "extension",
                    $"Extension {(x.Status == ExtensionStatus.Approved ? "approved" : x.Status == ExtensionStatus.Rejected ? "rejected" : Text.Humanize(x.Status).ToLowerInvariant())} by {P(x.DecidedById)?.Name ?? "the approver"}",
                    x.DecisionNote, P(x.DecidedById), "User", x.Id, new() { ["status"] = x.Status.ToString() }));
        }
        foreach (var x in followUps)
            timeline.Add(new TimelineItem(x.ProcessedAt, "followup", FollowUpTitle(x), null, null, "System", x.Id,
                new() { ["action"] = x.Action.ToString(), ["kind"] = x.Kind.ToString(), ["notifications"] = x.NotificationCount.ToString(CultureInfo.InvariantCulture) }));
        foreach (var x in sent)
            timeline.Add(new TimelineItem(x.SentAt ?? x.CreatedAt, "notification",
                $"{ChannelLabel(x.Channel)} sent to {x.RecipientName ?? "recipient"}: {x.Subject}", null, null, ChannelLabel(x.Channel), x.Id,
                new() { ["category"] = x.Category.ToString() }));
        foreach (var x in linkRows.Where(l => l.Status == LinkStatus.Linked))
            timeline.Add(new TimelineItem(x.SentAt, x.Channel == CommunicationChannel.Email ? "email" : "teams",
                x.Channel == CommunicationChannel.Email
                    ? $"Email from {x.SenderName ?? P(x.SenderEmployeeId)?.Name ?? "unknown"}: {x.Subject ?? "(no subject)"}"
                    : $"Teams message from {x.SenderName ?? P(x.SenderEmployeeId)?.Name ?? "unknown"}",
                x.BodyPreview ?? NotificationService.Shorten(x.Body.ReplaceLineEndings(" "), 300), P(x.SenderEmployeeId),
                x.Channel == CommunicationChannel.Email ? "Email" : "Teams", x.MessageId,
                new() { ["category"] = x.Category.ToString(), ["method"] = x.Method.ToString(), ["confidence"] = x.Confidence.ToString("0.00", CultureInfo.InvariantCulture) }));
        foreach (var x in submissions)
            timeline.Add(new TimelineItem(x.SubmittedAt, "completion", $"{PR(x.SubmittedById).Name} marked the task complete (round {x.Round})", x.Note,
                P(x.SubmittedById), x.Source.ToString(), x.Id,
                new() { ["evidence"] = (evidenceBySubmission.TryGetValue(x.Id, out var ev) ? ev.Count : 0).ToString(CultureInfo.InvariantCulture),
                    ["completionDate"] = x.CompletionDate.ToString("yyyy-MM-dd") }));
        foreach (var x in suggestions)
            timeline.Add(new TimelineItem(x.CreatedAt, "suggestion", $"AI suggestion: {x.Explanation}", x.Detail, null, "Ai", x.Id,
                new() { ["kind"] = x.Kind.ToString(), ["status"] = x.Status.ToString(), ["confidence"] = x.Confidence.ToString("0.00", CultureInfo.InvariantCulture) }));
        foreach (var x in auditRows)
            timeline.Add(new TimelineItem(x.At, x.Action == "TaskCreated" ? "created" : "change", AuditTitle(x, guidNames), x.Action == "TaskCreated" ? null : x.Details,
                P(x.ActorId), x.Source.ToString(), x.Id, x.Field is null ? null : new() { ["action"] = x.Action, ["field"] = x.Field }));

        timeline = [.. timeline.OrderByDescending(t => t.At).ThenBy(t => t.Kind).Take(500)];

        return new TaskDetail
        {
            Summary = summary,
            Description = task.Description,
            Objective = task.Objective,
            ExpectedResult = task.ExpectedResult,
            Deliverable = task.Deliverable,
            SuccessCriteria = task.SuccessCriteria,
            Links = task.Links,
            OriginalDelegator = P(task.OriginalDelegatorId),
            AccountableManager = P(task.AccountableManagerId),
            CreatedBy = P(task.CreatedById),
            Project = project,
            Category = category,
            Team = team,
            Department = department,
            TeamId = task.TeamId,
            DepartmentId = task.DepartmentId,
            ProjectId = task.ProjectId,
            CategoryId = task.CategoryId,
            AssignedAt = task.AssignedAt,
            StartDate = task.StartDate,
            DueTime = task.DueTime,
            HardDeadline = task.HardDeadline,
            ReviewDate = task.ReviewDate,
            OriginalDueDate = task.OriginalDueDate,
            DueDateChangeCount = task.DueDateChangeCount,
            DeadlineAt = task.DeadlineAt,
            Acknowledgement = task.Acknowledgement,
            AcknowledgedAt = task.AcknowledgedAt,
            AcknowledgementComment = task.AcknowledgementComment,
            RequiresApproval = task.RequiresApproval,
            CustomerImpact = task.CustomerImpact,
            CustomerName = task.CustomerName,
            FinancialImpact = task.FinancialImpact,
            NotifyCeoOnDelay = task.NotifyCeoOnDelay,
            Source = task.Source,
            AttentionFactors = task.AttentionFactors,
            FollowUp = new FollowUpInfo(task.FollowUp,
                task.FollowUp.Describe() + (task.FollowUpPausedUntil is { } pu && pu >= c.Today ? $" — paused until {pu.ToString("d MMM", CultureInfo.InvariantCulture)}" : ""),
                task.NextFollowUpAt, task.LastFollowUpAt, task.FollowUpPausedUntil, task.FollowUpPauseReason, task.FollowUpPauseNote,
                task.RemindersSinceResponse, task.LastReminderAt, task.NoResponseSince),
            Assignees = task.Assignees.Where(a => a.RemovedAt == null).OrderBy(a => a.Role).ThenBy(a => a.AssignedAt)
                .Select(a => new AssigneeDto(a.Id, a.EmployeeId, P(a.EmployeeId)?.Name, a.TeamId,
                    a.TeamId is { } at && teamNames.TryGetValue(at, out var n) ? n : null, a.Role, a.AssignedAt)).ToList(),
            Followers = task.Followers.Select(f => PR(f.EmployeeId)).OrderBy(p => p.Name).ToList(),
            Subtasks = subtasks,
            Dependencies = dependencies,
            Commitments = commitmentDtos,
            Approvals = approvals.Select(a => new ApprovalDto(a.Id, a.Round, a.Stage, a.ApproverRole, PR(a.ApproverId), a.Decision, a.Comment,
                a.RequestedAt, a.DecidedAt, CanDecideApproval(a))).ToList(),
            Extensions = extensions.Select(x => new ExtensionDto(x.Id, PR(x.RequestedById), x.OriginalDueDate, x.CurrentDueDate, x.ProposedDueDate, x.Reason,
                P(x.ApproverId), x.Status, x.DecisionNote, x.RequestedAt, x.DecidedAt, x.Source, CanDecideExtension(x))).ToList(),
            Suggestions = suggestions.Select(x => new SuggestionDto(x.Id, x.Kind, x.SuggestedStatus, x.SuggestedDate, x.Detail, x.Confidence, x.Explanation,
                x.Status, x.MessageId, x.CreatedAt)).ToList(),
            DelegationChain = delegations.Select(x => new DelegationDto(x.Sequence, P(x.FromEmployeeId), PR(x.ToEmployeeId), x.DelegatedAt, x.Note)).ToList(),
            Attachments = attachments.Select(x => new AttachmentDto(x.Id, x.FileName, x.ContentType, x.SizeBytes, x.UploadedAt, P(x.UploadedById), x.IsEvidence)).ToList(),
            Submissions = submissions.Select(x => new SubmissionDto(x.Id, x.Round, PR(x.SubmittedById), x.SubmittedAt, x.CompletionDate, x.Note, x.Source,
                (evidenceBySubmission.TryGetValue(x.Id, out var ev) ? ev : [])
                    .Select(e => new EvidenceDto(e.Id, e.Kind, e.Title, e.Url, e.Reference, e.AttachmentId, e.AddedAt)).ToList())).ToList(),
            Escalations = escalations.Select(x => new EscalationDto(x.Id, x.Level, x.Trigger, x.Targets, x.Reason, x.TriggeredAt, x.IsManual)).ToList(),
            Messages = messages,
            Timeline = timeline,
            Permissions = permissions,
            RecurringDefinitionId = task.RecurringDefinitionId,
            RecurringTitle = recurringTitle,
            OccurrenceKey = occurrenceKey,
            MeetingId = meetingId,
            MeetingTitle = meetingTitle,
            Parent = parent,
        };
    }

    // ── Detail helpers ──

    private sealed record DepRow(Guid Id, DependencyType Type, Guid? OtherId, string? ExternalDescription, string? ExternalParty, DateOnly? ExpectedBy,
        bool IsResolved, string? OtherNumber, string? OtherTitle, WorkStatus? OtherStatus);

    private sealed record SubRow(Guid Id, string Number, string Title, WorkStatus Status, Guid? ParentTaskId, Guid? AssigneeId, string? AssigneeName,
        DateOnly? DueDate, TimeOnly? DueTime, int ProgressPercent, TaskHealth Health, int Depth, bool IsBlocked, int SortOrder);

    /// <summary>
    /// Whether a dependency currently blocks the dependent task: external until resolved, finish-to-start until the
    /// predecessor closes, start-to-start until the predecessor has started.
    /// </summary>
    public static bool IsBlockingDependency(DependencyType type, Guid? predecessorId, WorkStatus? predecessorStatus, bool resolved)
    {
        if (resolved) return false;
        if (predecessorId is null) return true;
        if (predecessorStatus is not { } s) return false;
        return type == DependencyType.StartToStart
            ? s is WorkStatus.Draft or WorkStatus.Assigned or WorkStatus.AcknowledgementPending or WorkStatus.Accepted
            : !TaskStatusRules.IsClosed(s);
    }

    private static DependencyDto ToDependency(DepRow d, string direction, HashSet<Guid> visible, bool blocking)
    {
        var restricted = d.OtherId is { } o && !visible.Contains(o);
        return new DependencyDto(d.Id, d.Type, d.OtherId, d.OtherNumber, restricted ? "Restricted task" : d.OtherTitle, d.OtherStatus,
            d.ExternalDescription, d.ExternalParty, d.ExpectedBy, d.IsResolved, blocking, direction);
    }

    private static List<SubtaskNode> BuildTree(Guid rootId, List<SubRow> rows, int baseDepth, QueryClock c)
    {
        var byParent = rows.Where(r => r.ParentTaskId is not null).GroupBy(r => r.ParentTaskId!.Value).ToDictionary(g => g.Key, g => g.OrderBy(r => r.SortOrder).ThenBy(r => r.Number).ToList());
        List<SubtaskNode> Children(Guid parentId, int guard) =>
            guard > MaxSubtaskDepth || !byParent.TryGetValue(parentId, out var kids) ? [] : kids.Select(r =>
            {
                var overdue = DeadlineRules.IsOverdue(r.DueDate, r.DueTime, r.Status, c.Now, c.Calendar);
                return new SubtaskNode(r.Id, r.Number, r.Title, r.Status, overdue ? "Overdue" : Text.Humanize(r.Status),
                    r.AssigneeId is { } a ? new PersonRef(a, r.AssigneeName ?? "") : null, r.DueDate,
                    DeadlineRules.Describe(r.DueDate, r.DueTime, r.Status, c.Now, c.Calendar), r.ProgressPercent, r.Health, r.Depth - baseDepth,
                    r.ParentTaskId, r.IsBlocked || r.Status == WorkStatus.Blocked, Children(r.Id, guard + 1));
            }).ToList();
        return Children(rootId, 1);
    }

    private static string AckTitle(string who, AcknowledgementResponse r, DateOnly? proposed) => r switch
    {
        AcknowledgementResponse.Accept => $"{who} accepted the task",
        AcknowledgementResponse.NeedClarification => $"{who} asked for clarification",
        AcknowledgementResponse.CannotAccept => $"{who} could not accept the task",
        _ => $"{who} proposed a different deadline{(proposed is { } d ? $" ({d.ToString("ddd d MMM", CultureInfo.InvariantCulture)})" : "")}",
    };

    private static string ApprovalTitle(string who, ApprovalDecision d) => d switch
    {
        ApprovalDecision.Approved => $"{who} confirmed completion",
        ApprovalDecision.Reopened => $"{who} reopened the task",
        ApprovalDecision.MoreWorkRequested => $"{who} requested more work",
        _ => $"{who}: {Text.Humanize(d).ToLowerInvariant()}",
    };

    private static string FollowUpTitle(TaskFollowUpLog x)
    {
        var reason = x.Reason.Length > 0 ? char.ToLowerInvariant(x.Reason[0]) + x.Reason[1..] : x.Reason;
        return x.Action switch
        {
            FollowUpAction.Remind => $"Reminder sent: {reason}",
            FollowUpAction.Skip => $"Reminder skipped: {reason}",
            FollowUpAction.Stop => $"Follow-up stopped: {reason}",
            _ => $"Follow-up checked: {reason}",
        };
    }

    private static string ChannelLabel(NotificationChannel c) => c switch
    {
        NotificationChannel.Email => "Email",
        NotificationChannel.InApp => "In-app notice",
        _ => "Teams",
    };

    private static string Targets(EscalationTarget t) =>
        TaskMetrics.JoinAnd(Enum.GetValues<EscalationTarget>().Where(x => x != EscalationTarget.None && t.HasFlag(x))
            .Select(x => x == EscalationTarget.Ceo ? "the CEO" : Text.Humanize(x).ToLowerInvariant()).ToList()) is { Length: > 0 } s ? s : "nobody";

    private static string AuditTitle(AuditLog a, Dictionary<Guid, string> names)
    {
        string Value(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "—";
            if (Guid.TryParse(s, out var g)) return names.TryGetValue(g, out var n) ? n : "—";
            return s;
        }
        var field = a.Field is null ? null : Words(a.Field);
        return a.Action switch
        {
            "TaskCreated" => $"Task created by {a.ActorName}",
            "Reassigned" => $"Reassigned from {Value(a.OldValue)} to {Value(a.NewValue)}",
            "FollowUpPaused" => $"Follow-up paused until {Value(a.NewValue)}",
            "FollowUpResumed" => "Follow-up resumed",
            "AssigneeAdded" => $"{Value(a.NewValue)} added as {(a.Field ?? "assignee").ToLowerInvariant()}",
            "AssigneeRemoved" => $"{(a.Field ?? "Assignee")} removed",
            "DependencyAdded" => $"Dependency added: {Value(a.NewValue)}",
            "DependencyRemoved" => "Dependency removed",
            "DependencyResolved" => $"Dependency resolved{(a.Details is null ? "" : $": {a.Details}")}",
            "ProgressUpdated" => $"Progress updated from {Value(a.OldValue)}% to {Value(a.NewValue)}%",
            "Reopened" => a.NewValue is null ? "Task reopened" : $"{Value(a.NewValue)}",
            "AttachmentAdded" => $"Attachment added: {Value(a.NewValue)}",
            "LinkConfirmed" => "Message link confirmed",
            _ when a.Field is not null => $"{field} changed from {Value(a.OldValue)} to {Value(a.NewValue)}",
            _ => Words(a.Action),
        };
    }

    /// <summary>"DueDate" → "Due date".</summary>
    public static string Words(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 8);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]) && !char.IsUpper(pascal[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? pascal[i] : char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }

    /// <summary>Resolves GUID-valued audit fields (project, team, manager…) to names.</summary>
    private async Task<Dictionary<Guid, string>> GuidNamesAsync(IEnumerable<AuditLog> rows, Dictionary<Guid, PersonRef> people, CancellationToken ct)
    {
        var ids = rows.SelectMany(r => new[] { r.OldValue, r.NewValue }).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().ToList();
        var result = people.ToDictionary(p => p.Key, p => p.Value.Name);
        if (ids.Count == 0) return result;
        foreach (var x in await db.Projects.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)) result[x.Id] = x.Name;
        foreach (var x in await db.Teams.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)) result[x.Id] = x.Name;
        foreach (var x in await db.Departments.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)) result[x.Id] = x.Name;
        foreach (var x in await db.TaskCategories.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)) result[x.Id] = x.Name;
        foreach (var x in await db.EscalationPolicies.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)) result[x.Id] = x.Name;
        foreach (var x in await db.Employees.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.DisplayName }).ToListAsync(ct)) result[x.Id] = x.DisplayName;
        return result;
    }
}
