using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FollowUp.Core.Services;

/// <summary>
/// Recurring tasks as a first-class feature (spec §11–§15). Each occurrence becomes its own task; occurrences
/// are recorded (created / skipped / missed, always with a reason) under a unique (series, nominal date) key,
/// so generation is exactly-once and resumes from the durable checkpoint (LastProcessedOccurrence) after any
/// downtime, applying the definition's catch-up policy.
/// </summary>
public sealed class RecurringTaskService(
    FollowUpDbContext db,
    ActorContext actor,
    IClock clock,
    CalendarProvider calendars,
    TaskService tasks,
    MonitoringService monitoring,
    AuditService audit,
    ILogger<RecurringTaskService> logger) : IRecurringTaskService
{
    /// <summary>An occurrence processed more than this after its creation time counts as missed (system unavailable).</summary>
    private static readonly TimeSpan LateThreshold = TimeSpan.FromMinutes(30);

    // ═══════════════════════════════ Generation (job "recurrence.generate") ═══════════════════════════════

    public async Task<int> GenerateDueAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var ids = await db.RecurringDefinitions.Where(d => d.State != RecurrenceState.Ended && (d.NextOccurrenceAt == null || d.NextOccurrenceAt <= now))
            .Select(d => d.Id).ToListAsync(ct);
        var created = 0;
        foreach (var id in ids)
        {
            try { created += await ProcessDefinitionAsync(id, ct); }
            catch (DbUpdateException ex) when (Database.IsUniqueViolation(ex))
            {
                logger.LogInformation("Occurrence for recurring definition {Id} already generated (unique key) — continuing", id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken definition must not stop the others; it is retried on the next run.
                logger.LogError(ex, "Recurring definition {Id} could not be processed", id);
                db.ChangeTracker.Clear();
                db.IntegrationErrors.Add(new IntegrationError
                {
                    Integration = "Recurrence", SourceKey = id.ToString(), Operation = "Generate occurrence",
                    Kind = IntegrationErrorKind.Validation, Message = NotificationService.Shorten(ex.Message, 1900), OccurredAt = clock.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }
            finally { db.ChangeTracker.Clear(); }
        }
        return created;
    }

    private async Task<int> ProcessDefinitionAsync(Guid id, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        var today = cal.Today(now);
        var def = await db.RecurringDefinitions.FirstAsync(d => d.Id == id, ct);

        if (def.State == RecurrenceState.Paused && def.ResumeOn is { } resume && resume <= today)
        {
            def.State = RecurrenceState.Active;
            def.PauseReason = null;
            def.ResumeOn = null;
            audit.Record("RecurrenceResumed", nameof(RecurringTaskDefinition), def.Id, details: "Automatic resume date reached");
        }

        var from = def.LastProcessedOccurrence?.AddDays(1) ?? def.EffectiveFrom;
        if (from < def.EffectiveFrom) from = def.EffectiveFrom;
        var due = new List<Occurrence>();
        foreach (var occ in RecurrenceEngine.Between(def.Rule, def.Due, def.HolidayHandling, def.StartDate, def.EndDate, from, today.AddDays(1), cal))
        {
            if (occ.CreateAtUtc > now) break;          // keep the checkpoint monotonic
            due.Add(occ);
        }

        var generated = await db.RecurringInstances.CountAsync(i => i.SeriesId == def.SeriesId && (i.Outcome == OccurrenceOutcome.Created || i.Outcome == OccurrenceOutcome.CreatedLate), ct);
        var missed = due.Where(o => !o.IsSkipped && now - Max(o.CreateAtUtc, def.CreatedAt) > LateThreshold).ToList();
        var latestMissed = missed.LastOrDefault();
        var createdCount = 0;

        foreach (var occ in due)
        {
            if (await db.RecurringInstances.AnyAsync(i => i.SeriesId == def.SeriesId && i.NominalDate == occ.NominalDate, ct))
            {
                def.LastProcessedOccurrence = occ.NominalDate;      // already handled (e.g. skipped in advance by a user)
                continue;
            }
            var lateness = (int)Math.Max(0, (now - Max(occ.CreateAtUtc, def.CreatedAt)).TotalMinutes);
            var (outcome, reason) = Classify(def, occ, missed.Contains(occ), latestMissed, generated, now, today);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var instance = new RecurringTaskInstance
            {
                DefinitionId = def.Id,
                SeriesId = def.SeriesId,
                NominalDate = occ.NominalDate,
                OccurrenceDate = occ.Date,
                OccurrenceKey = occ.Key,
                ScheduledCreateAt = occ.CreateAtUtc,
                DueDate = occ.DueDate,
                Outcome = outcome,
                Reason = reason,
                ProcessedAt = now,
                LatenessMinutes = lateness,
            };
            db.RecurringInstances.Add(instance);
            if (outcome is OccurrenceOutcome.Created or OccurrenceOutcome.CreatedLate)
            {
                var task = await CreateInstanceTaskAsync(def, occ, instance, outcome == OccurrenceOutcome.CreatedLate, ct);   // saves instance + task together
                instance.TaskId = task.Id;
                def.GeneratedCount++;
                generated++;
                createdCount++;
            }
            else if (outcome == OccurrenceOutcome.SkippedCatchUp && def.Criticality >= Criticality.Important)
            {
                monitoring.UpsertAttention(AttentionType.RecurringMissed, $"recur:{def.SeriesId:N}:{occ.NominalDate:yyyyMMdd}", null,
                    $"Recurring task missed: {RecurrenceEngine.RenderTitle(def.TitleTemplate, occ, def.Rule)}", reason ?? "Skipped after downtime", 55);
            }
            if (outcome == OccurrenceOutcome.OutOfRange)
            {
                def.State = RecurrenceState.Ended;
                def.EndedAt = now;
            }
            def.LastProcessedOccurrence = occ.NominalDate;
            audit.Record($"Occurrence{outcome}", nameof(RecurringTaskDefinition), def.Id, instance.TaskId, "Occurrence", null, occ.Key, reason);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (def.State == RecurrenceState.Ended) break;
        }

        // Durable "next scheduled run" for this definition.
        if (def.State != RecurrenceState.Ended)
        {
            var next = RecurrenceEngine.Next(def.Rule, def.Due, def.HolidayHandling, def.StartDate, def.EndDate,
                def.LastProcessedOccurrence ?? def.EffectiveFrom.AddDays(-1), cal);
            def.NextOccurrenceAt = next?.CreateAtUtc;
            if (next is null)
            {
                def.State = RecurrenceState.Ended;
                def.EndedAt = now;
                audit.Record("RecurrenceEnded", nameof(RecurringTaskDefinition), def.Id, details: "No further occurrences");
            }
        }
        await db.SaveChangesAsync(ct);
        return createdCount;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>Decides what happens to one occurrence — never silently dropped (spec §15).</summary>
    private static (OccurrenceOutcome, string?) Classify(RecurringTaskDefinition def, Occurrence occ, bool isMissed, Occurrence? latestMissed,
        int generated, DateTime now, DateOnly today)
    {
        if (occ.IsSkipped) return (OccurrenceOutcome.SkippedHoliday, occ.SkipReason);
        if (def.State == RecurrenceState.Paused) return (OccurrenceOutcome.SkippedPaused, $"Recurrence paused{(def.PauseReason is { } r ? $": {r}" : "")}");
        if (def.MaxOccurrences is { } max && generated >= max) return (OccurrenceOutcome.OutOfRange, $"Maximum of {max} occurrences reached");
        if (!isMissed) return (OccurrenceOutcome.Created, null);
        var late = $"generated late after downtime (scheduled {occ.CreateAtUtc:u})";
        return def.CatchUp switch
        {
            CatchUpPolicy.GenerateAll => (OccurrenceOutcome.CreatedLate, late),
            CatchUpPolicy.GenerateLatestOnly => occ == latestMissed
                ? (OccurrenceOutcome.CreatedLate, late)
                : (OccurrenceOutcome.SkippedCatchUp, $"Missed while the system was unavailable; superseded by the {latestMissed?.Key} occurrence"),
            CatchUpPolicy.SkipMissed => occ.DueDate is { } d && d < today
                ? (OccurrenceOutcome.SkippedCatchUp, $"Missed while the system was unavailable; its due date ({d:d MMM}) has already passed")
                : (OccurrenceOutcome.CreatedLate, late),
            _ => (now - occ.CreateAtUtc).TotalDays <= def.CatchUpWindowDays
                ? (OccurrenceOutcome.CreatedLate, late)
                : (OccurrenceOutcome.SkippedCatchUp, $"Missed by more than the {def.CatchUpWindowDays}-day catch-up window"),
        };
    }

    private async Task<WorkTask> CreateInstanceTaskAsync(RecurringTaskDefinition def, Occurrence occ, RecurringTaskInstance instance, bool late, CancellationToken ct)
    {
        var title = RecurrenceEngine.RenderTitle(def.TitleTemplate, occ, def.Rule);
        var request = new CreateTaskRequest
        {
            Title = title,
            Description = (def.Description ?? "") + (late ? $"\n\n(Generated late: the system was unavailable at the scheduled time {occ.CreateAtUtc:u}.)" : ""),
            PrimaryAssigneeId = def.PrimaryAssigneeId,
            AdditionalAssigneeIds = def.AdditionalAssigneeIds,
            TeamId = def.TeamId,
            TeamIds = def.PrimaryAssigneeId is null && def.TeamId is { } t ? [t] : [],
            DepartmentId = def.DepartmentId,
            ProjectId = def.ProjectId,
            CategoryId = def.CategoryId,
            Priority = def.Priority,
            Criticality = def.Criticality,
            Visibility = def.Visibility,
            StartDate = occ.Date,
            DueDate = occ.DueDate,
            DueTime = occ.DueTime,
            FollowUp = def.FollowUp,
            RequiresApproval = def.RequiresApproval,
            RequiresAcknowledgement = def.RequiresAcknowledgement,
            EscalationPolicyId = def.EscalationPolicyId,
            Source = TaskSource.Recurring,
            Tags = ["recurring"],
        };
        var task = await tasks.CreateAsync(request, ct, def.OwnerId, t =>
        {
            t.RecurringDefinitionId = def.Id;
            t.RecurringInstanceId = instance.Id;
            t.TemplateId = def.TemplateId;
        });
        foreach (var item in def.Checklist.Where(c => !string.IsNullOrWhiteSpace(c)))
            await tasks.CreateAsync(new CreateTaskRequest
            {
                Title = item,
                ParentTaskId = task.Id,
                PrimaryAssigneeId = def.PrimaryAssigneeId ?? task.PrimaryAssigneeId,
                DueDate = occ.DueDate,
                Priority = def.Priority,
                Criticality = def.Criticality <= Criticality.Normal ? def.Criticality : Criticality.Normal,
                RequiresAcknowledgement = false,
                RequiresApproval = false,
                Source = TaskSource.Recurring,
            }, ct, def.OwnerId);
        return task;
    }

    // ═══════════════════════════════ Definitions ═══════════════════════════════

    public async Task<RecurringTaskDefinition> CreateAsync(RecurringDefinitionInput r, CancellationToken ct)
    {
        Validate(r);
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        var today = cal.Today(now);
        var def = new RecurringTaskDefinition();
        Apply(def, r);
        def.SeriesId = def.Id;
        def.OwnerId = r.OwnerId ?? actor.EmployeeId;
        def.Code = string.IsNullOrWhiteSpace(r.Code) ? $"REC-{now:yyMMdd}-{Random.Shared.Next(100, 999)}" : r.Code!;
        def.StartDate = r.StartDate ?? today;
        def.EffectiveFrom = def.StartDate;
        def.CreatedAt = now;
        // Backdated start dates align the cadence but never back-fill history the system never owed.
        if (def.StartDate < today) def.LastProcessedOccurrence = today.AddDays(-1);
        var next = RecurrenceEngine.Next(def.Rule, def.Due, def.HolidayHandling, def.StartDate, def.EndDate, def.LastProcessedOccurrence, cal);
        def.NextOccurrenceAt = next?.CreateAtUtc;
        db.RecurringDefinitions.Add(def);
        audit.Record("RecurrenceCreated", nameof(RecurringTaskDefinition), def.Id, details: $"{def.TitleTemplate} — {def.Rule.Describe()}");
        await db.SaveChangesAsync(ct);
        return def;
    }

    public async Task<RecurringTaskDefinition> UpdateAsync(Guid id, RecurringUpdateRequest r, CancellationToken ct)
    {
        Validate(r.Definition);
        var def = await db.RecurringDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Recurring definition");
        await RequireOwnerAsync(def, ct);
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);

        switch (r.Scope)
        {
            case ChangeScope.ThisInstance:
            {
                var instance = await db.RecurringInstances.FirstOrDefaultAsync(i => i.Id == r.FromInstanceId && i.SeriesId == def.SeriesId, ct)
                    ?? throw new ValidationException("instance_required", "Choose the occurrence to change.");
                if (instance.TaskId is not { } taskId) throw new ValidationException("no_task", "That occurrence has no task.");
                await ApplyToInstanceTaskAsync(taskId, r.Definition, ct);
                return def;
            }
            case ChangeScope.ThisAndFuture:
            {
                DateOnly fromDate;
                Guid? instanceTaskId = null;
                if (r.FromInstanceId is { } iid && await db.RecurringInstances.FirstOrDefaultAsync(i => i.Id == iid, ct) is { } inst)
                {
                    fromDate = inst.NominalDate;
                    instanceTaskId = inst.TaskId;
                }
                else fromDate = r.FromDate ?? cal.Today(now);

                // Split: the current version ends the day before; a new version (same series) takes over.
                var next = new RecurringTaskDefinition();
                Apply(next, r.Definition);
                next.SeriesId = def.SeriesId;
                next.Code = def.Code;
                next.OwnerId = r.Definition.OwnerId ?? def.OwnerId;
                next.PreviousVersionId = def.Id;
                var ruleChanged = System.Text.Json.JsonSerializer.Serialize(next.Rule) != System.Text.Json.JsonSerializer.Serialize(def.Rule);
                next.StartDate = ruleChanged ? fromDate : def.StartDate;
                next.EffectiveFrom = fromDate;
                next.LastProcessedOccurrence = def.LastProcessedOccurrence is { } lp && lp >= fromDate ? lp : fromDate.AddDays(-1);
                next.CreatedAt = def.CreatedAt;
                next.NextOccurrenceAt = RecurrenceEngine.Next(next.Rule, next.Due, next.HolidayHandling, next.StartDate, next.EndDate, next.LastProcessedOccurrence, cal)?.CreateAtUtc;
                def.EndDate = fromDate.AddDays(-1);
                def.State = RecurrenceState.Ended;
                def.EndedAt = now;
                db.RecurringDefinitions.Add(next);
                audit.Record("RecurrenceSplit", nameof(RecurringTaskDefinition), next.Id, details: $"Changes apply from {fromDate:d MMM yyyy}; previous version {def.Id}");
                await db.SaveChangesAsync(ct);
                if (instanceTaskId is { } tid) await ApplyToInstanceTaskAsync(tid, r.Definition, ct);
                return next;
            }
            default:
            {
                Apply(def, r.Definition);
                def.NextOccurrenceAt = RecurrenceEngine.Next(def.Rule, def.Due, def.HolidayHandling, def.StartDate, def.EndDate, def.LastProcessedOccurrence, cal)?.CreateAtUtc;
                audit.Record("RecurrenceUpdated", nameof(RecurringTaskDefinition), def.Id, details: "Entire series updated");
                await db.SaveChangesAsync(ct);
                var openTasks = await db.Tasks.OpenOnly().Where(t => t.RecurringDefinitionId == def.Id).Select(t => t.Id).ToListAsync(ct);
                foreach (var t in openTasks) await ApplyToInstanceTaskAsync(t, r.Definition, ct);
                return def;
            }
        }
    }

    private async Task ApplyToInstanceTaskAsync(Guid taskId, RecurringDefinitionInput d, CancellationToken ct)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.IsClosed) return;
        await tasks.UpdateAsync(taskId, new UpdateTaskRequest { Priority = d.Priority, Criticality = d.Criticality, ChangeReason = "Recurring definition changed" }, ct);
        if (d.PrimaryAssigneeId is { } p && p != task.PrimaryAssigneeId)
            await tasks.ReassignAsync(taskId, new ReassignRequest(p, "Recurring definition changed"), ct);
    }

    public async Task<RecurringTaskDefinition> PauseAsync(Guid id, RecurringPauseRequest r, CancellationToken ct)
    {
        var def = await db.RecurringDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Recurring definition");
        await RequireOwnerAsync(def, ct);
        if (def.State != RecurrenceState.Active) throw new ConflictException("not_active", "Only an active recurrence can be paused.");
        def.State = RecurrenceState.Paused;
        def.PausedAt = clock.UtcNow;
        def.PauseReason = r.Reason;
        def.ResumeOn = r.ResumeOn;
        audit.Record("RecurrencePaused", nameof(RecurringTaskDefinition), def.Id, details: r.Reason);
        await db.SaveChangesAsync(ct);
        return def;
    }

    public async Task<RecurringTaskDefinition> ResumeAsync(Guid id, CancellationToken ct)
    {
        var def = await db.RecurringDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Recurring definition");
        await RequireOwnerAsync(def, ct);
        if (def.State != RecurrenceState.Paused) throw new ConflictException("not_paused", "The recurrence is not paused.");
        var cal = await calendars.GetDefaultAsync(ct);
        def.State = RecurrenceState.Active;
        def.PauseReason = null;
        def.ResumeOn = null;
        def.NextOccurrenceAt = RecurrenceEngine.Next(def.Rule, def.Due, def.HolidayHandling, def.StartDate, def.EndDate, def.LastProcessedOccurrence, cal)?.CreateAtUtc;
        audit.Record("RecurrenceResumed", nameof(RecurringTaskDefinition), def.Id);
        await db.SaveChangesAsync(ct);
        return def;
    }

    public async Task<RecurringTaskDefinition> EndAsync(Guid id, string? reason, CancellationToken ct)
    {
        var def = await db.RecurringDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Recurring definition");
        await RequireOwnerAsync(def, ct);
        var cal = await calendars.GetDefaultAsync(ct);
        def.State = RecurrenceState.Ended;
        def.EndedAt = clock.UtcNow;
        def.EndDate = cal.Today(clock.UtcNow);
        def.NextOccurrenceAt = null;
        audit.Record("RecurrenceEnded", nameof(RecurringTaskDefinition), def.Id, details: reason);
        await db.SaveChangesAsync(ct);
        return def;
    }

    /// <summary>Skip the current or a future occurrence (spec §14). Earlier occurrences and other instances are untouched.</summary>
    public async Task<RecurringTaskInstance> SkipAsync(Guid id, SkipOccurrenceRequest r, CancellationToken ct)
    {
        var def = await db.RecurringDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Recurring definition");
        await RequireOwnerAsync(def, ct);
        var now = clock.UtcNow;
        var reason = string.IsNullOrWhiteSpace(r.Reason) ? $"Skipped by {actor.Name}" : $"Skipped by {actor.Name}: {r.Reason}";
        if (r.InstanceId is { } iid)
        {
            var inst = await db.RecurringInstances.FirstOrDefaultAsync(i => i.Id == iid && i.SeriesId == def.SeriesId, ct) ?? throw new NotFoundException("Occurrence");
            if (inst.TaskId is { } taskId && await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == taskId && t.Status != WorkStatus.Completed && t.Status != WorkStatus.Cancelled, ct))
                await tasks.ChangeStatusAsync(taskId, new StatusChangeRequest(WorkStatus.Cancelled, reason), ct);
            inst.Outcome = OccurrenceOutcome.SkippedByUser;
            inst.Reason = reason;
            await db.SaveChangesAsync(ct);
            return inst;
        }
        if (r.NominalDate is not { } nominal) throw new ValidationException("occurrence_required", "Choose the occurrence to skip.");
        if (await db.RecurringInstances.AnyAsync(i => i.SeriesId == def.SeriesId && i.NominalDate == nominal, ct))
            throw new ConflictException("already_processed", "That occurrence has already been generated — skip its task instead.");
        var cal = await calendars.GetDefaultAsync(ct);
        var occ = RecurrenceEngine.Resolve(def.Rule, def.Due, def.HolidayHandling, nominal, null, cal);
        var skipped = new RecurringTaskInstance
        {
            DefinitionId = def.Id, SeriesId = def.SeriesId, NominalDate = nominal, OccurrenceDate = occ.Date, OccurrenceKey = occ.Key,
            ScheduledCreateAt = occ.CreateAtUtc, DueDate = occ.DueDate, Outcome = OccurrenceOutcome.SkippedByUser, Reason = reason, ProcessedAt = now,
        };
        db.RecurringInstances.Add(skipped);
        audit.Record("OccurrenceSkippedByUser", nameof(RecurringTaskDefinition), def.Id, details: $"{occ.Key}: {reason}");
        await db.SaveChangesAsync(ct);
        return skipped;
    }

    public async Task<List<OccurrencePreview>> PreviewAsync(PreviewRequest r, CancellationToken ct)
    {
        var cal = await calendars.GetDefaultAsync(ct);
        var today = cal.Today(clock.UtcNow);
        var start = r.StartDate ?? today;
        var from = start < today ? today : start;
        return RecurrenceEngine.Preview(r.Rule, r.Due, r.HolidayHandling, start, r.EndDate, from, Math.Clamp(r.Count, 1, 24), cal)
            .Select(o => new OccurrencePreview(o.NominalDate, o.Date, o.CreateAtUtc, o.DueDate, o.DueTime, o.Key,
                RecurrenceEngine.RenderTitle(r.TitleTemplate ?? "{Date}", o, r.Rule),
                o.IsSkipped ? o.SkipReason : o.Date != o.NominalDate ? $"Moved from {o.NominalDate:ddd d MMM} ({cal.NonWorkingReason(o.NominalDate)})" : null))
            .ToList();
    }

    // ═══════════════════════════════ Queries ═══════════════════════════════

    public async Task<List<RecurringDefinitionDto>> ListAsync(bool includeEnded, CancellationToken ct, Guid? teamId = null)
    {
        var q = db.RecurringDefinitions.AsNoTracking().AsQueryable();
        if (!includeEnded) q = q.Where(d => d.State != RecurrenceState.Ended);
        if (teamId is { } t) q = q.Where(d => d.TeamId == t);
        if (!actor.Viewer.IsExecutive && actor.EmployeeId is { } me)
        {
            var teams = actor.Viewer.TeamIds.ToList();
            var depts = actor.Viewer.ManagedDepartmentIds.ToList();
            q = q.Where(d => d.OwnerId == me || d.PrimaryAssigneeId == me || (d.TeamId != null && teams.Contains(d.TeamId.Value))
                || (d.DepartmentId != null && depts.Contains(d.DepartmentId.Value)));
        }
        var defs = await q.OrderBy(d => d.TitleTemplate).ToListAsync(ct);
        return await ToDtosAsync(defs, ct);
    }

    public async Task<RecurringDefinitionDto> GetAsync(Guid id, CancellationToken ct)
    {
        var def = await db.RecurringDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Recurring definition");
        return (await ToDtosAsync([def], ct))[0];
    }

    public async Task<List<RecurringDefinitionDto>> ToDtosAsync(List<RecurringTaskDefinition> defs, CancellationToken ct)
    {
        var cal = await calendars.GetDefaultAsync(ct);
        var now = clock.UtcNow;
        var today = cal.Today(now);
        var seriesIds = defs.Select(d => d.SeriesId).Distinct().ToList();
        var stats = await db.RecurringInstances.Where(i => seriesIds.Contains(i.SeriesId))
            .Select(i => new
            {
                i.SeriesId, i.Outcome, i.DueDate, i.TaskId,
                Status = db.Tasks.Where(t => t.Id == i.TaskId).Select(t => (WorkStatus?)t.Status).FirstOrDefault(),
                CompletedAt = db.Tasks.Where(t => t.Id == i.TaskId).Select(t => t.CompletedAt).FirstOrDefault(),
            }).ToListAsync(ct);
        var peopleIds = defs.SelectMany(d => new[] { d.OwnerId, d.PrimaryAssigneeId }.Concat(d.AdditionalAssigneeIds.Select(x => (Guid?)x))).Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        var people = await db.Employees.Where(e => peopleIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);
        var teams = await db.Teams.ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var projects = await db.Projects.ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        PersonRef? P(Guid? id) => id is { } x && people.TryGetValue(x, out var n) ? new PersonRef(x, n) : null;
        NamedRef? N(Guid? id, Dictionary<Guid, string> map) => id is { } x && map.TryGetValue(x, out var n) ? new NamedRef(x, n) : null;

        return defs.Select(d =>
        {
            var s = stats.Where(x => x.SeriesId == d.SeriesId).ToList();
            var created = s.Where(x => x.Outcome is OccurrenceOutcome.Created or OccurrenceOutcome.CreatedLate).ToList();
            var onTime = created.Count(x => x.CompletedAt is { } ca && x.DueDate is { } dd && DateOnly.FromDateTime(cal.ToLocal(ca)) <= dd);
            var late = created.Count(x => x.CompletedAt is { } ca && x.DueDate is { } dd && DateOnly.FromDateTime(cal.ToLocal(ca)) > dd);
            var pastDue = created.Count(x => x.DueDate is { } dd && dd < today && x.Status is not WorkStatus.Cancelled);
            var missed = s.Count(x => x.Outcome == OccurrenceOutcome.SkippedCatchUp)
                + created.Count(x => x.DueDate is { } dd && dd < today && x.CompletedAt is null && x.Status is not WorkStatus.Cancelled);
            return new RecurringDefinitionDto
            {
                Id = d.Id, SeriesId = d.SeriesId, Code = d.Code, TitleTemplate = d.TitleTemplate, Description = d.Description,
                Owner = P(d.OwnerId), PrimaryAssignee = P(d.PrimaryAssigneeId), AdditionalAssignees = d.AdditionalAssigneeIds.Select(x => P(x)).Where(x => x is not null).Select(x => x!).ToList(),
                Team = N(d.TeamId, teams), Department = N(d.DepartmentId, depts), Project = N(d.ProjectId, projects),
                Priority = d.Priority, Criticality = d.Criticality, Visibility = d.Visibility,
                Rule = d.Rule, RuleText = d.Rule.Describe(), Due = d.Due, DueText = d.Due.Describe(),
                FollowUp = d.FollowUp, FollowUpText = d.FollowUp.Describe(), EscalationPolicyId = d.EscalationPolicyId,
                HolidayHandling = d.HolidayHandling, CatchUp = d.CatchUp, CatchUpWindowDays = d.CatchUpWindowDays,
                RequiresApproval = d.RequiresApproval, RequiresAcknowledgement = d.RequiresAcknowledgement, Checklist = d.Checklist,
                StartDate = d.StartDate, EndDate = d.EndDate, MaxOccurrences = d.MaxOccurrences, EffectiveFrom = d.EffectiveFrom,
                State = d.State, PauseReason = d.PauseReason, ResumeOn = d.ResumeOn, LastProcessedOccurrence = d.LastProcessedOccurrence,
                NextOccurrenceAt = d.NextOccurrenceAt, GeneratedCount = created.Count, MissedCount = missed,
                OpenInstances = created.Count(x => x.Status is { } st && !TaskStatusRules.IsClosed(st)),
                CompletedOnTime = onTime, CompletedLate = late,
                CompliancePercent = pastDue == 0 ? 100 : (int)Math.Round(100.0 * onTime / Math.Max(pastDue, onTime)),
            };
        }).ToList();
    }

    public async Task<List<RecurringInstanceDto>> InstancesAsync(Guid? definitionId, IReadOnlyCollection<OccurrenceOutcome>? outcomes, DateOnly? from, DateOnly? to,
        bool missedOnly, CancellationToken ct, int take = 300)
    {
        var cal = await calendars.GetDefaultAsync(ct);
        var today = cal.Today(clock.UtcNow);
        var q = db.RecurringInstances.AsNoTracking().AsQueryable();
        if (definitionId is { } d)
        {
            var series = await db.RecurringDefinitions.Where(x => x.Id == d).Select(x => x.SeriesId).FirstOrDefaultAsync(ct);
            q = q.Where(i => i.SeriesId == series);
        }
        if (outcomes is { Count: > 0 }) q = q.Where(i => outcomes.Contains(i.Outcome));
        if (from is { } f) q = q.Where(i => i.NominalDate >= f);
        if (to is { } t) q = q.Where(i => i.NominalDate <= t);
        var rows = await q.OrderByDescending(i => i.NominalDate).Take(take).Select(i => new
        {
            i, Title = db.RecurringDefinitions.Where(x => x.Id == i.DefinitionId).Select(x => x.TitleTemplate).FirstOrDefault(),
            Crit = db.RecurringDefinitions.Where(x => x.Id == i.DefinitionId).Select(x => x.Criticality).FirstOrDefault(),
            Number = db.Tasks.Where(x => x.Id == i.TaskId).Select(x => x.Number).FirstOrDefault(),
            Status = db.Tasks.Where(x => x.Id == i.TaskId).Select(x => (WorkStatus?)x.Status).FirstOrDefault(),
            CompletedAt = db.Tasks.Where(x => x.Id == i.TaskId).Select(x => x.CompletedAt).FirstOrDefault(),
        }).ToListAsync(ct);
        var list = rows.Select(r => new RecurringInstanceDto(r.i.Id, r.i.DefinitionId, r.Title ?? "", r.i.NominalDate, r.i.OccurrenceDate, r.i.OccurrenceKey,
            r.i.ScheduledCreateAt, r.i.DueDate, r.i.Outcome, r.i.Reason, r.i.TaskId, r.Number, r.Status, r.i.ProcessedAt, r.i.LatenessMinutes, r.Crit)).ToList();
        if (missedOnly)
            list = list.Where(x => x.Outcome == OccurrenceOutcome.SkippedCatchUp
                || (x.Outcome is OccurrenceOutcome.Created or OccurrenceOutcome.CreatedLate && x.DueDate is { } dd && dd < today
                    && x.TaskStatus is { } st && st != WorkStatus.Completed && st != WorkStatus.Cancelled)).ToList();
        return list;
    }

    // ═══════════════════════════════ Helpers ═══════════════════════════════

    private static void Validate(RecurringDefinitionInput r)
    {
        if (string.IsNullOrWhiteSpace(r.TitleTemplate)) throw new ValidationException("title_required", "Give the recurring task a title.");
        if (r.PrimaryAssigneeId is null && r.TeamId is null) throw new ValidationException("assignee_required", "Choose who does the recurring task.");
        if (r.Rule.Frequency == RecurrenceFrequency.AnchorDates && r.Rule.AnchorDates.Count == 0) throw new ValidationException("anchor_required", "Add at least one anchor date (e.g. the licence expiry).");
        if (r.Rule.Interval < 1) throw new ValidationException("interval", "Interval must be at least 1.");
        if (r.EndDate is { } e && r.StartDate is { } s && e < s) throw new ValidationException("dates", "End date is before start date.");
    }

    private static void Apply(RecurringTaskDefinition d, RecurringDefinitionInput r)
    {
        d.TitleTemplate = r.TitleTemplate.Trim();
        d.Description = r.Description;
        d.PrimaryAssigneeId = r.PrimaryAssigneeId;
        d.AdditionalAssigneeIds = r.AdditionalAssigneeIds;
        d.TeamId = r.TeamId;
        d.DepartmentId = r.DepartmentId;
        d.ProjectId = r.ProjectId;
        d.CategoryId = r.CategoryId;
        d.Priority = r.Priority;
        d.Criticality = r.Criticality;
        d.Visibility = r.Visibility;
        d.Rule = r.Rule;
        d.Due = r.Due;
        d.FollowUp = r.FollowUp ?? FollowUpSchedule.NoneSchedule;
        d.EscalationPolicyId = r.EscalationPolicyId;
        d.HolidayHandling = r.HolidayHandling;
        d.CatchUp = r.CatchUp;
        d.CatchUpWindowDays = Math.Max(1, r.CatchUpWindowDays);
        d.RequiresApproval = r.RequiresApproval;
        d.RequiresAcknowledgement = r.RequiresAcknowledgement;
        d.Checklist = r.Checklist;
        if (r.EndDate is { } end) d.EndDate = end;
        d.MaxOccurrences = r.MaxOccurrences;
    }

    private async Task RequireOwnerAsync(RecurringTaskDefinition def, CancellationToken ct)
    {
        if (actor.IsSystem || actor.Viewer.IsExecutive || def.OwnerId == actor.EmployeeId) return;
        if (def.TeamId is { } t && actor.Viewer.LedTeamIds.Contains(t)) return;
        if (def.DepartmentId is { } d && actor.Viewer.ManagedDepartmentIds.Contains(d)) return;
        await Task.CompletedTask;
        _ = ct;
        throw new ForbiddenException("Only the owner or a manager can change this recurring task.");
    }
}
