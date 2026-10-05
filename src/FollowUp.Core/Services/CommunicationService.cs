using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Services;

/// <summary>
/// Email and Teams communication pipeline (spec §21–§29, §33):
/// ingest (exactly once) → correlate to tasks (task number → known thread → reply context → participants /
/// keywords → AI semantic match) → classify and extract facts → apply effects (activity, commitments,
/// status, extension requests, completion suggestions, attention items). Low-confidence matches go to a
/// review queue instead of being attached. Each message is its own unit of work with retry and dead-letter.
/// </summary>
public sealed class CommunicationService(
    FollowUpDbContext db,
    IClock clock,
    CalendarProvider calendars,
    IAIClassificationService ai,
    ICommitmentExtractionService commitmentExtractor,
    TaskService tasks,
    MonitoringService monitoring,
    NotificationService notifications,
    AuditService audit,
    ActorContext actor,
    IOptions<FollowUpOptions> options,
    ILogger<CommunicationService> logger)
{
    private readonly FollowUpOptions _o = options.Value;
    private const int MaxAttempts = 6;
    private Dictionary<string, Guid>? _byEmail;
    private Dictionary<string, Guid>? _byExternal;

    // ═══════════════════════════════ Ingest ═══════════════════════════════

    /// <summary>Stores a synchronised message unless it already exists. Returns true when newly imported.</summary>
    public async Task<bool> IngestAsync(InboundMessage m, CancellationToken ct)
    {
        if (m.IsDeleted || string.IsNullOrEmpty(m.ExternalId)) return false;
        var key = string.IsNullOrEmpty(m.ConversationKey) ? (m.Channel == CommunicationChannel.Email ? "mail" : m.ConversationId ?? "") : m.ConversationKey;
        if (db.Messages.Local.Any(x => x.Channel == m.Channel && x.ConversationKey == key && x.ExternalId == m.ExternalId)
            || await db.Messages.AnyAsync(x => x.Channel == m.Channel && x.ConversationKey == key && x.ExternalId == m.ExternalId, ct))
            return false;

        await LoadDirectoryAsync(ct);
        var senderId = Resolve(m.FromAddress, m.FromUserId);
        var recipients = m.To.Concat(m.Cc).Select(p => p with { EmployeeId = Resolve(p.Address, p.ExternalUserId) }).ToList();
        var mentions = m.Mentions.Select(p => p with { EmployeeId = Resolve(p.Address, p.ExternalUserId) }).ToList();
        m.Headers.TryGetValue("X-FollowUp-Key", out var notificationKey);
        var systemGenerated = notificationKey is not null
            || IsSystemSender(m.FromAddress) || IsSystemSender(m.FromUserId)
            || m.BodyText.Contains("— Digital Follow-up Officer", StringComparison.Ordinal);
        var internalOnly = senderId is not null && recipients.All(r => r.EmployeeId is not null);
        var now = clock.UtcNow;

        db.Messages.Add(new CommunicationMessage
        {
            Channel = m.Channel,
            ConversationKey = key,
            ExternalId = m.ExternalId.Length > 300 ? m.ExternalId[..300] : m.ExternalId,
            GraphItemId = m.GraphItemId is { Length: > 400 } g ? g[..400] : m.GraphItemId,
            ConversationId = m.ConversationId,
            ThreadRootExternalId = m.ThreadRootId,
            ReplyToExternalId = m.ReplyToId,
            SourceKey = m.SourceKey,
            Subject = m.Subject is { Length: > 500 } s ? s[..500] : m.Subject,
            BodyText = m.BodyText,
            BodyPreview = NotificationService.Shorten(m.BodyText.ReplaceLineEndings(" ").Trim(), 480),
            SenderEmployeeId = senderId,
            SenderAddress = m.FromAddress,
            SenderName = m.FromName,
            Recipients = recipients,
            Mentions = mentions,
            Attachments = [.. m.Attachments],
            Direction = senderId is null ? MessageDirection.Inbound : internalOnly ? MessageDirection.Internal : MessageDirection.Outbound,
            SentAt = m.SentAt,
            ImportedAt = now,
            WebUrl = m.WebUrl,
            Importance = m.Importance,
            IsSystemGenerated = systemGenerated,
            NotificationKey = notificationKey,
            ProcessingStatus = systemGenerated ? MessageProcessingStatus.Ignored : MessageProcessingStatus.Pending,
        });
        return true;
    }

    private bool IsSystemSender(string? value) =>
        value is not null && _o.SystemSenders.Any(s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase));

    private async Task LoadDirectoryAsync(CancellationToken ct)
    {
        if (_byEmail is not null) return;
        var people = await db.Employees.AsNoTracking().Select(e => new { e.Id, e.Email, e.ExternalUserId }).ToListAsync(ct);
        _byEmail = people.Where(p => !string.IsNullOrEmpty(p.Email)).GroupBy(p => p.Email.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Id);
        _byExternal = people.Where(p => !string.IsNullOrEmpty(p.ExternalUserId)).GroupBy(p => p.ExternalUserId!).ToDictionary(g => g.Key, g => g.First().Id);
    }

    private Guid? Resolve(string? address, string? externalId)
    {
        if (externalId is not null && _byExternal!.TryGetValue(externalId, out var a)) return a;
        if (address is not null && _byEmail!.TryGetValue(address.ToLowerInvariant(), out var b)) return b;
        return null;
    }

    // ═══════════════════════════════ Processing (job "communication.process") ═══════════════════════════════

    public async Task<int> ProcessPendingAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var ids = await db.Messages.Where(m => m.ProcessingStatus == MessageProcessingStatus.Pending && (m.NextProcessingAt == null || m.NextProcessingAt <= now))
            .OrderBy(m => m.SentAt).Select(m => m.Id).Take(_o.ProcessingBatchSize).ToListAsync(ct);
        var processed = 0;
        foreach (var id in ids)
        {
            try
            {
                await ProcessOneAsync(id, ct);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                var msg = await db.Messages.FirstAsync(m => m.Id == id, ct);
                msg.ProcessingAttempts++;
                msg.ProcessingError = NotificationService.Shorten(ex.Message, 1900);
                if (msg.ProcessingAttempts >= MaxAttempts) msg.ProcessingStatus = MessageProcessingStatus.Failed;   // dead letter
                else msg.NextProcessingAt = clock.UtcNow.AddMinutes(Math.Pow(2, msg.ProcessingAttempts));
                logger.LogWarning(ex, "Processing message {Id} failed (attempt {Attempt})", id, msg.ProcessingAttempts);
                await db.SaveChangesAsync(ct);
            }
            finally { db.ChangeTracker.Clear(); }
        }
        return processed;
    }

    private sealed record Candidate(WorkTask Task, MatchMethod Method, double Prior, List<string> Signals);

    private async Task ProcessOneAsync(Guid id, CancellationToken ct)
    {
        var msg = await db.Messages.FirstAsync(m => m.Id == id, ct);
        if (msg.ProcessingStatus != MessageProcessingStatus.Pending) return;
        var cal = await calendars.GetDefaultAsync(ct);
        var now = clock.UtcNow;
        var analysis = await ToAnalysisAsync(msg, ct);

        // 1. Deterministic correlation (spec §22 / §26 order).
        var deterministic = new List<Candidate>();
        var numbers = TaskNumbers.FindAll($"{msg.Subject} {msg.BodyText}");
        if (numbers.Count > 0)
            foreach (var t in await db.Tasks.Include(t => t.Assignees).Where(t => numbers.Contains(t.Number)).ToListAsync(ct))
                deterministic.Add(new(t, MatchMethod.TaskNumber, 0.99, [$"mentions {t.Number}"]));
        if (deterministic.Count == 0 && msg.ConversationId is { } conv)
        {
            var threadKey = msg.Channel == CommunicationChannel.TeamsChannel && msg.ThreadRootExternalId is { } root
                ? $"{msg.ConversationKey}|{root}" : conv;
            var linked = await db.TaskConversations.Where(c => c.Channel == msg.Channel && (c.ConversationId == conv || c.ConversationId == threadKey))
                .OrderByDescending(c => c.Strength).Select(c => c.TaskId).ToListAsync(ct);
            foreach (var t in await db.Tasks.Include(t => t.Assignees).Where(t => linked.Contains(t.Id)).ToListAsync(ct))
                deterministic.Add(new(t, MatchMethod.ConversationThread, 0.95, ["same conversation thread as earlier task communication"]));
        }

        var links = new List<(Candidate Candidate, double Confidence, string Explanation, List<string> Signals, LinkStatus Status)>();
        if (deterministic.Count > 0)
        {
            foreach (var c in deterministic.DistinctBy(c => c.Task.Id))
                links.Add((c, c.Prior, c.Signals[0], c.Signals, LinkStatus.Linked));
        }
        else
        {
            // 2. Candidate generation: recent reminder context, participants, keywords, project.
            var candidates = await CandidatesAsync(msg, ct);
            if (candidates.Count > 0)
            {
                IReadOnlyList<CandidateScore> scores;
                try
                {
                    scores = await ai.MatchAsync(analysis, candidates.Select(c => ToCandidate(c)).ToList(), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "AI matching failed for message {Id}; using deterministic priors", msg.Id);
                    scores = candidates.Select(c => new CandidateScore(c.Task.Id, c.Prior, string.Join("; ", c.Signals), c.Signals)).ToList();
                }
                foreach (var c in candidates)
                {
                    var score = scores.FirstOrDefault(s => s.TaskId == c.Task.Id);
                    var conf = Math.Clamp(Math.Max(score?.Confidence ?? 0, c.Method == MatchMethod.RecentContext ? c.Prior : 0), 0, 1);
                    var signals = c.Signals.Concat(score?.Signals ?? []).Distinct().ToList();
                    var explanation = score?.Explanation ?? string.Join("; ", c.Signals);
                    if (conf >= _o.SuggestConfidence) links.Add((c with { Method = score is not null && score.Confidence >= c.Prior ? MatchMethod.Semantic : c.Method }, conf, explanation, signals, LinkStatus.Suggested));
                }
                links = links.OrderByDescending(l => l.Confidence).Take(3).ToList();
                if (links.Count > 0 && links[0].Confidence >= _o.AutoAttachConfidence
                    && (links.Count == 1 || links[0].Confidence - links[1].Confidence >= 0.1))
                    links[0] = links[0] with { Status = LinkStatus.Linked };
            }
        }

        // 3. Classification — in the context of the best task.
        var best = links.OrderByDescending(l => l.Status == LinkStatus.Linked).ThenByDescending(l => l.Confidence).FirstOrDefault();
        MessageClassification? cls = null;
        try { cls = await ai.ClassifyAsync(analysis, best.Candidate is null ? null : ToCandidate(best.Candidate), cal, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "AI classification failed for message {Id}", msg.Id); }
        if (cls is not null)
        {
            msg.Category = cls.Category;
            msg.CategoryConfidence = cls.Confidence;
            msg.AnalysisSummary = NotificationService.Shorten(cls.Summary, 1000);
            msg.Facts = cls.Facts;
        }

        foreach (var l in links)
        {
            db.TaskMessageLinks.Add(new TaskCommunicationLink
            {
                TaskId = l.Candidate.Task.Id,
                MessageId = msg.Id,
                Method = l.Candidate.Method,
                Confidence = Math.Round(l.Confidence, 3),
                Category = cls?.Category ?? MessageCategory.Unclassified,
                Explanation = NotificationService.Shorten(l.Explanation, 1000),
                Signals = l.Signals,
                Status = l.Status,
                CreatedAt = now,
            });
        }

        using (actor.AsSystem(msg.Channel == CommunicationChannel.Email ? ChangeSource.Email : ChangeSource.Teams))
        {
            foreach (var l in links.Where(l => l.Status == LinkStatus.Linked))
                await ApplyEffectsAsync(l.Candidate.Task, msg, cls, l.Confidence, ct);
        }

        // Critical message to an executive mailbox that matched no task (customer escalation etc.).
        if (!links.Any(l => l.Status == LinkStatus.Linked) && cls is { } c2 && (c2.Facts.Urgent || c2.Category == MessageCategory.Escalation)
            && await IsExecutiveSourceAsync(msg, ct))
            monitoring.UpsertAttention(msg.SenderEmployeeId is null ? AttentionType.CustomerEscalation : AttentionType.CriticalMessage, $"msg:{msg.Id:N}", null,
                $"{(msg.SenderEmployeeId is null ? "External escalation" : "Critical message")}: {msg.Subject ?? NotificationService.Shorten(msg.BodyPreview ?? "", 80)}",
                $"From {msg.SenderName ?? msg.SenderAddress}: {c2.Summary}", 70, a => a.MessageId = msg.Id);

        msg.ProcessingStatus = links.Any(l => l.Status == LinkStatus.Linked) || links.Count == 0 ? MessageProcessingStatus.Processed : MessageProcessingStatus.NeedsReview;
        msg.ProcessedAt = now;
        msg.ProcessingError = null;
        UiEvents.Publish(db, clock, UiEvents.CommunicationImported, links.FirstOrDefault(l => l.Status == LinkStatus.Linked).Candidate?.Task.Id);
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> IsExecutiveSourceAsync(CommunicationMessage msg, CancellationToken ct)
    {
        var execs = await monitoring.ExecutivesAsync(ct);
        if (msg.Recipients.Any(r => r.EmployeeId is { } e && execs.Contains(e))) return true;
        var owner = await db.SyncCheckpoints.Where(c => c.SourceKey == msg.SourceKey).Select(c => c.OwnerEmployeeId).FirstOrDefaultAsync(ct);
        return owner is { } o && execs.Contains(o);
    }

    private async Task<List<Candidate>> CandidatesAsync(CommunicationMessage msg, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Candidate>();
        var now = clock.UtcNow;

        // Reply context: a Teams/email message from someone we reminded about exactly one task recently.
        if (msg.SenderEmployeeId is { } sender)
        {
            var since = msg.SentAt.AddHours(-72);
            var reminded = await db.Notifications.Where(n => n.RecipientEmployeeId == sender && n.TaskId != null && n.Status == NotificationStatus.Sent
                    && n.SentAt >= since && n.SentAt <= msg.SentAt
                    && (msg.Channel == CommunicationChannel.Email ? n.Channel == NotificationChannel.Email : n.Channel == NotificationChannel.TeamsChat))
                .OrderByDescending(n => n.SentAt).Select(n => n.TaskId!.Value).Distinct().Take(5).ToListAsync(ct);
            var openReminded = await db.Tasks.Include(t => t.Assignees).OpenOnly().Where(t => reminded.Contains(t.Id)).ToListAsync(ct);
            foreach (var t in openReminded)
                result[t.Id] = new(t, MatchMethod.RecentContext, openReminded.Count == 1 ? 0.82 : 0.45, ["reply shortly after a reminder about this task"]);

            // Participants: open tasks the sender is responsible for or delegated.
            var involved = await db.Tasks.Include(t => t.Assignees).ActiveOnly()
                .Where(t => t.PrimaryAssigneeId == sender || t.DelegatorId == sender || t.Assignees.Any(a => a.EmployeeId == sender && a.RemovedAt == null))
                .OrderByDescending(t => t.LastActivityAt).Take(25).ToListAsync(ct);
            foreach (var t in involved)
                if (!result.ContainsKey(t.Id))
                    result[t.Id] = new(t, MatchMethod.Participants, t.PrimaryAssigneeId == sender ? 0.3 : 0.15,
                        [t.PrimaryAssigneeId == sender ? "sender is the responsible person" : t.DelegatorId == sender ? "sender delegated this task" : "sender is an assignee"]);
        }

        // Recipients that are responsible people (e.g. the CEO writing to an assignee).
        var recipientIds = msg.Recipients.Concat(msg.Mentions).Where(r => r.EmployeeId is not null).Select(r => r.EmployeeId!.Value).Distinct().ToList();
        if (recipientIds.Count > 0)
        {
            var forRecipients = await db.Tasks.Include(t => t.Assignees).ActiveOnly().Where(t => t.PrimaryAssigneeId != null && recipientIds.Contains(t.PrimaryAssigneeId.Value))
                .OrderByDescending(t => t.LastActivityAt).Take(20).ToListAsync(ct);
            foreach (var t in forRecipients)
                if (!result.ContainsKey(t.Id)) result[t.Id] = new(t, MatchMethod.Participants, 0.1, ["addressed to the responsible person"]);
        }

        // Project names mentioned in the text.
        var text = $"{msg.Subject} {msg.BodyText}".ToLowerInvariant();
        var projects = await db.Projects.Where(p => p.IsActive).Select(p => new { p.Id, p.Name, p.Code }).ToListAsync(ct);
        var mentioned = projects.Where(p => text.Contains(p.Name.ToLowerInvariant()) || text.Contains(p.Code.ToLowerInvariant())).Select(p => p.Id).ToList();
        if (mentioned.Count > 0)
            foreach (var t in await db.Tasks.Include(t => t.Assignees).ActiveOnly().Where(t => t.ProjectId != null && mentioned.Contains(t.ProjectId.Value)).Take(20).ToListAsync(ct))
                if (!result.ContainsKey(t.Id)) result[t.Id] = new(t, MatchMethod.Project, 0.15, ["mentions the task's project"]);
        _ = now;
        return result.Values.Take(30).ToList();
    }

    private TaskCandidate ToCandidate(Candidate c)
    {
        var t = c.Task;
        var names = db.Employees.Local.Where(e => t.Assignees.Any(a => a.EmployeeId == e.Id) || e.Id == t.PrimaryAssigneeId).Select(e => e.DisplayName).ToList();
        if (names.Count == 0)
        {
            var ids = t.Assignees.Where(a => a.EmployeeId is not null).Select(a => a.EmployeeId!.Value).Append(t.PrimaryAssigneeId ?? Guid.Empty).ToList();
            names = db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).Select(e => e.DisplayName).ToList();
        }
        var project = t.ProjectId is { } p ? db.Projects.AsNoTracking().Where(x => x.Id == p).Select(x => x.Name).FirstOrDefault() : null;
        var tags = db.TaskTags.AsNoTracking().Where(x => x.TaskId == t.Id).Select(x => x.Tag!.Name).ToList();
        return new TaskCandidate(t.Id, t.Number, t.Title, t.Description, project, names, tags, t.DueDate, t.Status, c.Prior, c.Signals);
    }

    private async Task<MessageForAnalysis> ToAnalysisAsync(CommunicationMessage msg, CancellationToken ct)
    {
        var ids = msg.Recipients.Where(r => r.EmployeeId is not null).Select(r => r.EmployeeId!.Value).ToList();
        var names = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).Select(e => e.DisplayName).ToListAsync(ct);
        return new MessageForAnalysis(msg.Id, msg.Channel, msg.Subject, msg.BodyText, msg.SenderName, msg.SenderEmployeeId, msg.SentAt, names);
    }

    // ═══════════════════════════════ Effects ═══════════════════════════════

    private async Task ApplyEffectsAsync(WorkTask task, CommunicationMessage msg, MessageClassification? cls, double confidence, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var source = msg.Channel switch
        {
            CommunicationChannel.Email => CommunicationSource.Email,
            CommunicationChannel.TeamsChat => CommunicationSource.TeamsChat,
            _ => CommunicationSource.TeamsChannel,
        };
        var channelName = msg.Channel == CommunicationChannel.Email ? "email" : "Teams";
        var responsible = msg.SenderEmployeeId is { } s && await tasks.IsResponsibleAsync(task, s, ct);
        var senderName = msg.SenderName ?? msg.SenderAddress ?? "someone";

        if (responsible)
        {
            TaskService.RecordAssigneeActivity(task, msg.SentAt > now ? now : msg.SentAt, channelName);
            if (cls is not null) task.LastUpdateSummary = NotificationService.Shorten($"{cls.Summary} ({channelName}, {msg.SentAt:d MMM})", 1000);
        }
        else if (task.LastActivityAt is null || msg.SentAt > task.LastActivityAt) task.LastActivityAt = msg.SentAt;

        // Keep strongly associated conversations as task threads (spec §24). A 1:1 chat spans many topics, so it is not pinned.
        if (msg.ConversationId is { } conv && msg.Channel != CommunicationChannel.TeamsChat && confidence >= 0.8)
        {
            var threadId = msg.Channel == CommunicationChannel.TeamsChannel && msg.ThreadRootExternalId is { } root ? $"{msg.ConversationKey}|{root}" : conv;
            var thread = db.TaskConversations.Local.FirstOrDefault(c => c.TaskId == task.Id && c.Channel == msg.Channel && c.ConversationId == threadId)
                ?? await db.TaskConversations.FirstOrDefaultAsync(c => c.TaskId == task.Id && c.Channel == msg.Channel && c.ConversationId == threadId, ct);
            if (thread is null)
                db.TaskConversations.Add(new TaskConversation { TaskId = task.Id, Channel = msg.Channel, ConversationId = threadId, Title = msg.Subject, Method = MatchMethod.ConversationThread, Strength = confidence, MessageCount = 1, LinkedAt = now, LastMessageAt = msg.SentAt });
            else { thread.MessageCount++; if (msg.SentAt > thread.LastMessageAt) thread.LastMessageAt = msg.SentAt; }
        }
        audit.Record(msg.Channel == CommunicationChannel.Email ? "EmailLinked" : "TeamsMessageLinked", nameof(CommunicationMessage), msg.Id, task.Id,
            "Category", null, cls?.Category.ToString(), $"{senderName}: {NotificationService.Shorten(msg.BodyPreview ?? "", 200)}");

        // Commitments made by the responsible side (spec §28).
        if (responsible)
        {
            IReadOnlyList<ExtractedCommitment> found = [];
            try
            {
                var cal = await calendars.GetDefaultAsync(ct);
                found = await commitmentExtractor.ExtractAsync(msg.BodyText, msg.SentAt, cal, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Commitment extraction failed for {Id}", msg.Id); }
            var best = found.Where(f => f.Confidence >= 0.5).OrderByDescending(f => f.Confidence).FirstOrDefault();
            if (best is not null)
                await tasks.AddCommitmentInternalAsync(task, msg.SenderEmployeeId, best.Date, best.Time, best.Text, best.What, source, msg.Id, null, best.Confidence, msg.SentAt, ct);
            else if (cls?.Facts.CommitmentDate is { } cd)
                await tasks.AddCommitmentInternalAsync(task, msg.SenderEmployeeId, cd, null, cls.Facts.CommitmentText ?? msg.BodyPreview ?? "", null, source, msg.Id, null, cls.Confidence, msg.SentAt, ct);
        }
        if (cls is null) { await monitoring.RefreshAsync(task, ct); return; }

        var facts = cls.Facts;
        // Completion reported → suggestion only; AI never closes work by itself (spec §33).
        if (cls.Category == MessageCategory.Completion && responsible && task.IsOpen && !TaskStatusRules.IsAwaitingConfirmation(task.Status))
        {
            if (!await db.TaskSuggestions.AnyAsync(x => x.TaskId == task.Id && x.Kind == SuggestionKind.MarkCompletedByAssignee && x.Status == SuggestionStatus.Pending, ct))
            {
                db.TaskSuggestions.Add(new TaskSuggestion
                {
                    TaskId = task.Id, MessageId = msg.Id, Kind = SuggestionKind.MarkCompletedByAssignee, SuggestedStatus = WorkStatus.CompletedByAssignee,
                    SuggestedDate = DateOnly.FromDateTime(msg.SentAt), Detail = NotificationService.Shorten(msg.BodyPreview ?? "", 900),
                    Confidence = cls.Confidence, Explanation = $"{senderName} reported completion via {channelName}: {cls.Summary}", CreatedAt = now,
                });
                if (task.DelegatorId is { } del)
                    await notifications.EnqueueAsync(new NoticeRequest
                    {
                        Key = $"completion-reported:{task.Id:N}:{msg.Id:N}",
                        Category = NotificationCategory.StatusChange,
                        Task = task,
                        RecipientIds = [del],
                        Headline = $"— {senderName} reported it as complete via {channelName}. Confirm to close it.",
                        Message = cls.Summary,
                        Channels = NotifyChannels.InApp,
                    }, ct);
            }
        }
        else if (facts.SuggestedStatus is { } status && responsible && task.IsOpen && status != task.Status
                 && status is not (WorkStatus.Completed or WorkStatus.CompletedByAssignee or WorkStatus.CompletionAwaitingApproval or WorkStatus.Cancelled)
                 && TaskStatusRules.CanTransition(task.Status, status))
        {
            // Non-closing status changes from the responsible person are applied (and shown as AI-applied).
            var auto = cls.Confidence >= 0.75;
            db.TaskSuggestions.Add(new TaskSuggestion
            {
                TaskId = task.Id, MessageId = msg.Id, Kind = SuggestionKind.ChangeStatus, SuggestedStatus = status,
                Detail = facts.Blocker ?? cls.Summary, Confidence = cls.Confidence,
                Explanation = $"{senderName} via {channelName}: {cls.Summary}", Status = auto ? SuggestionStatus.AutoApplied : SuggestionStatus.Pending, CreatedAt = now,
            });
            if (auto)
            {
                tasks.ChangeStatusInternal(task, status, $"From {channelName} message by {senderName}: {NotificationService.Shorten(cls.Summary, 300)}", now);
                if (TaskStatusRules.IsWaiting(status))
                {
                    task.IsBlocked = true;
                    task.BlockedSince ??= now;
                    task.BlockedReason = facts.Blocker is { } b ? $"Waiting for {b}" : task.BlockedReason;
                }
            }
        }
        if (facts.ProgressPercent is { } pct && responsible && pct > task.ProgressPercent && pct < 100) task.ProgressPercent = pct;

        if (facts.ProposedDueDate is { } proposed && responsible && task.IsOpen && proposed != task.DueDate && msg.SenderEmployeeId is { } requester)
            await tasks.CreateExtensionAsync(task, requester, proposed, $"Requested via {channelName}: {NotificationService.Shorten(msg.BodyPreview ?? "", 400)}", source, now, ct);

        var important = task.Criticality >= Criticality.Important || task.NotifyCeoOnDelay;
        if (important && cls.Category == MessageCategory.Delay)
            monitoring.UpsertAttention(AttentionType.CriticalMessage, $"delay:{msg.Id:N}", task, $"Delay reported: {task.Title}", $"{senderName} via {channelName}: {cls.Summary}", task.AttentionScore + 5, a => a.MessageId = msg.Id);
        if ((facts.Urgent || cls.Category == MessageCategory.Escalation) && (important || msg.SenderEmployeeId is null))
            monitoring.UpsertAttention(msg.SenderEmployeeId is null ? AttentionType.CustomerEscalation : AttentionType.CriticalMessage, $"msg:{msg.Id:N}", task,
                $"{(msg.SenderEmployeeId is null ? "Customer escalation" : "Critical message")}: {task.Title}", $"{senderName}: {cls.Summary}", task.AttentionScore + 8, a => a.MessageId = msg.Id);
        if (cls.Category is MessageCategory.RequestForHelp or MessageCategory.Clarification && responsible && task.DelegatorId is { } d)
        {
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = $"help:{task.Id:N}:{msg.Id:N}",
                Category = NotificationCategory.StatusChange,
                Task = task,
                RecipientIds = [d],
                Headline = cls.Category == MessageCategory.RequestForHelp ? $"— {senderName} asked for help." : $"— {senderName} asked for clarification.",
                Message = cls.Summary,
                Channels = NotifyChannels.InApp,
            }, ct);
            if (task.Criticality >= Criticality.Critical)
                monitoring.UpsertAttention(AttentionType.CriticalMessage, $"help:{msg.Id:N}", task, $"Help requested on critical task: {task.Title}", $"{senderName}: {cls.Summary}", task.AttentionScore + 6, a => a.MessageId = msg.Id);
        }

        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
    }

    // ═══════════════════════════════ Review queue & manual linking (spec §27) ═══════════════════════════════

    public async Task DecideLinkAsync(Guid linkId, bool confirm, CancellationToken ct)
    {
        var link = await db.TaskMessageLinks.Include(l => l.Message).FirstOrDefaultAsync(l => l.Id == linkId, ct) ?? throw new NotFoundException("Suggested link");
        if (!await CanSeeMessageAsync(link.MessageId, ct)) throw new NotFoundException("Suggested link");
        var now = clock.UtcNow;
        link.Status = confirm ? LinkStatus.Linked : LinkStatus.Rejected;
        link.DecidedAt = now;
        link.DecidedById = actor.EmployeeId;
        audit.Record(confirm ? "LinkConfirmed" : "LinkRejected", nameof(TaskCommunicationLink), link.Id, link.TaskId, details: link.Explanation);
        var msg = link.Message!;
        if (confirm)
        {
            var task = await db.Tasks.Include(t => t.Assignees).FirstAsync(t => t.Id == link.TaskId, ct);
            var cls = msg.Facts is null ? null : new MessageClassification(msg.Category, msg.CategoryConfidence ?? 0.5, msg.AnalysisSummary ?? "", msg.Facts, []);
            using (actor.AsSystem(ChangeSource.User)) await ApplyEffectsAsync(task, msg, cls, 1.0, ct);
        }
        var stillPending = await db.TaskMessageLinks.AnyAsync(l => l.MessageId == msg.Id && l.Id != link.Id && l.Status == LinkStatus.Suggested, ct);
        if (confirm || !stillPending) msg.ProcessingStatus = MessageProcessingStatus.Processed;
        if (confirm)
            foreach (var other in await db.TaskMessageLinks.Where(l => l.MessageId == msg.Id && l.Id != link.Id && l.Status == LinkStatus.Suggested).ToListAsync(ct))
                other.Status = LinkStatus.Rejected;
        UiEvents.Publish(db, clock, UiEvents.CommunicationImported, link.TaskId);
        await db.SaveChangesAsync(ct);
    }

    public async Task LinkManuallyAsync(Guid taskId, Guid messageId, CancellationToken ct)
    {
        var task = await tasks.LoadAsync(taskId, ct);
        if (!await CanSeeMessageAsync(messageId, ct)) throw new NotFoundException("Message");
        var msg = await db.Messages.FirstAsync(m => m.Id == messageId, ct);
        var existing = await db.TaskMessageLinks.FirstOrDefaultAsync(l => l.TaskId == taskId && l.MessageId == messageId, ct);
        if (existing is not null) { await DecideLinkAsync(existing.Id, true, ct); return; }
        var link = new TaskCommunicationLink
        {
            TaskId = taskId, MessageId = messageId, Method = MatchMethod.Manual, Confidence = 1, Category = msg.Category,
            Explanation = $"Linked manually by {actor.Name}", Signals = ["manual"], Status = LinkStatus.Linked, CreatedAt = clock.UtcNow,
            DecidedAt = clock.UtcNow, DecidedById = actor.EmployeeId,
        };
        db.TaskMessageLinks.Add(link);
        var cls = msg.Facts is null ? null : new MessageClassification(msg.Category, msg.CategoryConfidence ?? 0.5, msg.AnalysisSummary ?? "", msg.Facts, []);
        using (actor.AsSystem(ChangeSource.User)) await ApplyEffectsAsync(task, msg, cls, 1.0, ct);
        msg.ProcessingStatus = MessageProcessingStatus.Processed;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReprocessAsync(Guid messageId, CancellationToken ct)
    {
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct) ?? throw new NotFoundException("Message");
        msg.ProcessingStatus = MessageProcessingStatus.Pending;
        msg.ProcessingAttempts = 0;
        msg.NextProcessingAt = null;
        msg.ProcessingError = null;
        await db.TaskMessageLinks.Where(l => l.MessageId == messageId && l.Status == LinkStatus.Suggested).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    // ═══════════════════════════════ Queries ═══════════════════════════════

    /// <summary>Messages the viewer may see: linked to a visible task, sent by them, or in a mailbox / chat they own (spec §70).</summary>
    public IQueryable<CommunicationMessage> VisibleMessages()
    {
        if (actor.IsSystem || actor.Viewer.IsExecutive) return db.Messages;
        var me = actor.EmployeeId;
        var visibleTasks = db.Tasks.VisibleTo(db, actor.Viewer).Select(t => t.Id);
        var owned = db.SyncCheckpoints.Where(c => c.OwnerEmployeeId == me).Select(c => c.SourceKey);
        return db.Messages.Where(m => m.SenderEmployeeId == me || owned.Contains(m.SourceKey)
            || db.TaskMessageLinks.Any(l => l.MessageId == m.Id && l.Status == LinkStatus.Linked && visibleTasks.Contains(l.TaskId)));
    }

    private Task<bool> CanSeeMessageAsync(Guid id, CancellationToken ct) => VisibleMessages().AnyAsync(m => m.Id == id, ct);

    public async Task<PagedResult<MessageDto>> ListAsync(MessageFilter f, CancellationToken ct)
    {
        var q = VisibleMessages().AsNoTracking();
        if (f.Channel is { } ch) q = q.Where(m => m.Channel == ch);
        if (f.Status is { } st) q = q.Where(m => m.ProcessingStatus == st);
        if (f.Unlinked == true) q = q.Where(m => !db.TaskMessageLinks.Any(l => l.MessageId == m.Id && l.Status == LinkStatus.Linked));
        if (!string.IsNullOrWhiteSpace(f.Text)) q = q.Where(m => (m.Subject != null && m.Subject.Contains(f.Text)) || m.BodyText.Contains(f.Text) || (m.SenderName != null && m.SenderName.Contains(f.Text)));
        if (f.From is { } from) { var fu = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc); q = q.Where(m => m.SentAt >= fu); }
        if (f.To is { } to) { var tu = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc); q = q.Where(m => m.SentAt < tu); }
        var total = await q.CountAsync(ct);
        var page = Math.Max(1, f.Page);
        var size = Math.Clamp(f.PageSize, 1, 200);
        var rows = await q.OrderByDescending(m => m.SentAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<MessageDto>(await ToDtosAsync(rows, ct), total, page, size);
    }

    public async Task<MessageDto> GetAsync(Guid id, CancellationToken ct)
    {
        var msg = await VisibleMessages().AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("Message");
        return (await ToDtosAsync([msg], ct))[0];
    }

    public async Task<List<MessageDto>> ReviewQueueAsync(CancellationToken ct)
    {
        var rows = await VisibleMessages().AsNoTracking().Where(m => m.ProcessingStatus == MessageProcessingStatus.NeedsReview)
            .OrderByDescending(m => m.SentAt).Take(100).ToListAsync(ct);
        return await ToDtosAsync(rows, ct);
    }

    private async Task<List<MessageDto>> ToDtosAsync(List<CommunicationMessage> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var links = await db.TaskMessageLinks.AsNoTracking().Where(l => ids.Contains(l.MessageId) && l.Status != LinkStatus.Rejected)
            .Select(l => new { l.Id, l.MessageId, l.TaskId, l.Task!.Number, l.Task.Title, l.Method, l.Confidence, l.Explanation, l.Signals, l.Status })
            .ToListAsync(ct);
        var senderIds = rows.Where(r => r.SenderEmployeeId is not null).Select(r => r.SenderEmployeeId!.Value).Distinct().ToList();
        var senders = await db.Employees.AsNoTracking().Where(e => senderIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => new PersonRef(e.Id, e.DisplayName, e.JobTitle, e.Email), ct);
        return rows.Select(m => new MessageDto(m.Id, m.Channel, m.Subject, m.BodyText, m.SenderName, m.SenderAddress,
            m.SenderEmployeeId is { } s && senders.TryGetValue(s, out var p) ? p : null, m.Recipients, m.SentAt, m.ImportedAt, m.Direction,
            m.ProcessingStatus, m.Category, m.CategoryConfidence, m.AnalysisSummary, m.Facts, m.SourceKey, m.WebUrl, m.IsSystemGenerated,
            links.Where(l => l.MessageId == m.Id).Select(l => new LinkedTaskRef(l.Id, l.TaskId, l.Number, l.Title, l.Method, l.Confidence, l.Explanation, l.Signals, l.Status)).ToList()))
            .ToList();
    }
}
