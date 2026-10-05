using System.Globalization;
using System.Text.RegularExpressions;
using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FollowUp.Core.Services;

/// <summary>
/// Natural-language questions (spec §50, "Ask Follow-up Officer") and natural-language / voice delegation
/// (spec §48, §49). The AI port only turns text into a structured query or draft; Core executes the query against
/// the viewer's visible data and answers in one deterministic sentence with a drill-down.
/// </summary>
public sealed partial class AskService(
    FollowUpDbContext db,
    ActorContext actor,
    TaskQueryService queries,
    AttentionService attention,
    DigestService digest,
    CommitmentService commitments,
    RecurringTaskService recurring,
    INaturalLanguageService nl,
    ILogger<AskService> logger)
{
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Day(DateOnly d) => d.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    // ═══════════════════════════════ Directory for name resolution ═══════════════════════════════

    [GeneratedRegex(@"\bteam\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TeamWord();

    [GeneratedRegex(@"\(([^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Parenthesised();

    /// <summary>
    /// People (aliases: first name, job title, email local part), teams (name without "Team", department name),
    /// departments (code) and visible projects (code and any words in parentheses).
    /// </summary>
    public async Task<DirectorySnapshot> DirectoryAsync(CancellationToken ct)
    {
        var people = await db.Employees.AsNoTracking().Where(e => e.IsActive)
            .Select(e => new { e.Id, e.DisplayName, e.JobTitle, e.Email, e.DepartmentId }).ToListAsync(ct);
        var teams = await db.Teams.AsNoTracking().Where(t => t.IsActive)
            .Select(t => new { t.Id, t.Name, t.DepartmentId, t.LeaderId, Dept = t.Department != null ? t.Department.Name : null }).ToListAsync(ct);
        var depts = await db.Departments.AsNoTracking().Where(d => d.IsActive).Select(d => new { d.Id, d.Name, d.Code }).ToListAsync(ct);
        var projects = await db.Projects.AsNoTracking().Where(p => p.IsActive).VisibleProjects(actor.Viewer)
            .Select(p => new { p.Id, p.Name, p.Code }).ToListAsync(ct);

        static List<string> Aliases(string name, params string?[] candidates) =>
            candidates.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!.Trim())
                .Where(a => a.Length > 1 && !string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new DirectorySnapshot(
            people.Select(e => new DirectoryPerson(e.Id, e.DisplayName, e.JobTitle, e.Email, e.DepartmentId,
                Aliases(e.DisplayName, e.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), e.JobTitle,
                    e.Email.Contains('@') ? e.Email[..e.Email.IndexOf('@')] : null))).ToList(),
            teams.Select(t => new DirectoryTeamRef(t.Id, t.Name, t.DepartmentId, t.LeaderId,
                Aliases(t.Name, Regex.Replace(TeamWord().Replace(t.Name, " "), @"\s+", " ").Trim(), t.Dept))).ToList(),
            depts.Select(d => new DirectoryRef(d.Id, d.Name, Aliases(d.Name, d.Code))).ToList(),
            projects.Select(p => new DirectoryRef(p.Id, p.Name,
                Aliases(p.Name, [p.Code, Parenthesised().Replace(p.Name, "").Trim(), .. Parenthesised().Matches(p.Name).Select(m => m.Groups[1].Value)]))).ToList(),
            actor.EmployeeId);
    }

    private string Provider()
    {
        try { return nl.ProviderName; } catch (Exception) { return "Unavailable"; }
    }

    // ═══════════════════════════════ Questions (spec §50) ═══════════════════════════════

    public async Task<AskAnswer> AskAsync(string question, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ValidationException("question_required", "Ask a question.");
        question = question.Trim();
        var c = await queries.ClockAsync(ct);
        var dir = await DirectoryAsync(ct);
        QuestionQuery q;
        try { q = await nl.ParseQuestionAsync(question, dir, c.Now, c.Calendar, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Question parsing failed; falling back to a text search");
            q = new QuestionQuery { Intent = QuestionIntent.Unknown, SearchText = question, Interpretation = "Searched tasks for the question text" };
        }
        var provider = Provider();
        var scope = new Scope(
            q.DepartmentId is { } d ? dir.Departments.FirstOrDefault(x => x.Id == d)?.Name : null,
            q.TeamId is { } t ? dir.Teams.FirstOrDefault(x => x.Id == t)?.Name : null,
            q.EmployeeId is { } e ? dir.People.FirstOrDefault(x => x.Id == e)?.DisplayName : null,
            q.ProjectId is { } p ? dir.Projects.FirstOrDefault(x => x.Id == p)?.Name : null);

        TaskFilter Scoped(TaskFilter f) => f with
        {
            DepartmentId = q.DepartmentId ?? f.DepartmentId,
            TeamId = q.TeamId ?? f.TeamId,
            AssignedToId = q.EmployeeId ?? f.AssignedToId,
            ProjectId = q.ProjectId ?? f.ProjectId,
            MinCriticality = q.MinCriticality ?? f.MinCriticality,
            PageSize = 50,
        };
        string Interp(string fallback) => string.IsNullOrWhiteSpace(q.Interpretation) ? fallback : q.Interpretation;
        async Task<AskAnswer> Tasks(TaskFilter f, Func<int, string> sentence, string interpretation)
        {
            var page = await queries.QueryAsync(f, ct);
            return new AskAnswer(question, q, Interp(interpretation), sentence(page.Total), [.. page.Items], [], [], [], TaskDrill(f), provider);
        }
        string Period(DateOnly? from, DateOnly? to) =>
            from is { } f && to is { } tt ? $" between {Day(f)} and {Day(tt)}" : from is { } f2 ? $" since {Day(f2)}" : to is { } t2 ? $" up to {Day(t2)}" : "";
        var crit = q.MinCriticality is { } mc && mc >= Criticality.Important ? Text.Humanize(mc).ToLowerInvariant() : null;

        switch (q.Intent)
        {
            case QuestionIntent.Overdue:
            {
                var min = q.MinDaysOverdue is { } m && m > 0 ? m : (int?)null;
                var f = Scoped(new TaskFilter { Overdue = true, MinDaysOverdue = min, Sort = "due" });
                var by = min is > 1 ? $" by more than {min - 1} days" : "";
                return await Tasks(f, n => $"{scope.Noun(n, crit)} {Is(n)} overdue{by}.", $"Overdue tasks{scope.Describe()}{by}");
            }
            case QuestionIntent.NoResponse:
            {
                var f = Scoped(new TaskFilter { NoResponse = true, Sort = "attention" });
                return await Tasks(f, n => $"{scope.Noun(n, crit)} {(n == 1 ? "has" : "have")} had no response after a reminder.", $"Tasks with no response{scope.Describe()}");
            }
            case QuestionIntent.Blocked:
            {
                var f = Scoped(new TaskFilter { Blocked = true, Sort = "attention" });
                return await Tasks(f, n => $"{scope.Noun(n, crit)} {Is(n)} blocked.", $"Blocked tasks{scope.Describe()}");
            }
            case QuestionIntent.DueSoon:
            {
                var from = q.From ?? c.Today;
                var to = q.To ?? c.Today.AddDays(7);
                var f = Scoped(new TaskFilter { Open = true, Overdue = false, DueFrom = from, DueTo = to, Sort = "due" });
                return await Tasks(f, n => $"{scope.Noun(n, crit)} {Is(n)} due by {Day(to)}.", $"Open tasks{scope.Describe()} due between {Day(from)} and {Day(to)}");
            }
            case QuestionIntent.Critical:
            {
                var f = Scoped(new TaskFilter { Open = true, Sort = "attention" }) with { MinCriticality = q.MinCriticality ?? Criticality.Critical };
                var adjective = Text.Humanize(f.MinCriticality!.Value).ToLowerInvariant();
                return await Tasks(f, n => $"{scope.Noun(n, adjective)} {Is(n)} open.", $"Open {adjective} tasks{scope.Describe()}");
            }
            case QuestionIntent.CompletedRecently:
            {
                var from = q.From ?? c.Today.AddDays(-7);
                var f = Scoped(new TaskFilter { Statuses = [WorkStatus.Completed], CompletedFrom = from, CompletedTo = q.To, Sort = "completed" });
                return await Tasks(f, n => $"{scope.Noun(n, crit)} {(n == 1 ? "was" : "were")} completed{Period(from, q.To)}.", $"Completed tasks{scope.Describe()}{Period(from, q.To)}");
            }
            case QuestionIntent.AwaitingApproval:
            {
                var f = Scoped(new TaskFilter { AwaitingApproval = true, WaitingForCeo = q.ToMe ? true : null, Sort = "updated" });
                return await Tasks(f, n => $"{scope.Noun(n, crit)} {Is(n)} awaiting completion confirmation{(q.ToMe ? " from you" : "")}.",
                    $"Completions awaiting confirmation{scope.Describe()}");
            }
            case QuestionIntent.AttentionRequired:
            {
                var items = await attention.ListAsync("Open", ct, 50, q.MinCriticality);
                if (scope.Department is { } dn) items = items.Where(a => a.Task?.DepartmentName == dn).ToList();
                if (scope.Team is { } tn) items = items.Where(a => a.Task?.TeamName == tn).ToList();
                if (q.EmployeeId is { } emp) items = items.Where(a => a.Task?.PrimaryAssignee?.Id == emp).ToList();
                var n = items.Count;
                var answer = n == 0 ? $"Nothing{scope.Describe()} needs your attention right now."
                    : $"{n} {TaskMetrics.Plural(n, "item needs", "items need")} your attention{scope.Describe()}; the most urgent is \"{items[0].Title}\".";
                return new AskAnswer(question, q, Interp($"Open attention items{scope.Describe()}"), answer, [], [], [], items,
                    Drilldown.To("/attention", ("status", "Open")), provider);
            }
            case QuestionIntent.MissedCommitments:
            case QuestionIntent.CommitmentsDue:
            {
                var missed = q.Intent == QuestionIntent.MissedCommitments;
                var from = q.From ?? (missed ? (DateOnly?)null : c.Today);
                var to = q.To ?? (missed ? (DateOnly?)null : c.Today.AddDays(7));
                var list = await commitments.ListAsync(missed ? CommitmentStatus.Missed : CommitmentStatus.Open, q.ToMe, q.EmployeeId, from, to, ct);
                if (q.DepartmentId is not null || q.TeamId is not null || q.ProjectId is not null)
                {
                    var ids = list.Select(x => x.TaskId).Distinct().ToList();
                    var scoped = await (await queries.FilterAsync(Scoped(new TaskFilter()), c, ct)).Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct);
                    list = list.Where(x => scoped.Contains(x.TaskId)).ToList();
                }
                var n = list.Count;
                var who = q.ToMe ? " made to you" : scope.Employee is { } en ? $" by {en}" : "";
                var answer = missed
                    ? $"{(n == 0 ? "No" : n.ToString(CultureInfo.InvariantCulture))} {TaskMetrics.Plural(n, "commitment")}{who}{scope.DescribeTasks()} {(n == 1 ? "was" : "were")} missed{Period(from, to)}."
                    : $"{(n == 0 ? "No" : n.ToString(CultureInfo.InvariantCulture))} open {TaskMetrics.Plural(n, "commitment")}{who}{scope.DescribeTasks()} {(n == 1 ? "is" : "are")} due{Period(from, to)}.";
                var drill = new List<(string, string)> { ("status", missed ? "Missed" : "Open") };
                if (q.ToMe) drill.Add(("toMe", "true"));
                if (q.EmployeeId is { } eid) drill.Add(("employeeId", eid.ToString()));
                if (from is { } fd) drill.Add(("from", D(fd)));
                if (to is { } td) drill.Add(("to", D(td)));
                return new AskAnswer(question, q, Interp($"{(missed ? "Missed" : "Open")} commitments{who}{Period(from, to)}"), answer, [], list, [], [],
                    Drilldown.To("/commitments", [.. drill]), provider);
            }
            case QuestionIntent.RecurringMissed:
            {
                var from = q.From ?? c.Today.AddDays(-30);
                var to = q.To ?? c.Today;
                var instances = await recurring.InstancesAsync(null, null, from, to, missedOnly: true, ct);
                var n = instances.Count;
                return new AskAnswer(question, q, Interp($"Missed recurring occurrences{Period(from, to)}"),
                    $"{(n == 0 ? "No" : n.ToString(CultureInfo.InvariantCulture))} recurring {TaskMetrics.Plural(n, "occurrence")} {(n == 1 ? "was" : "were")} missed{Period(from, to)}.",
                    [], [], instances, [], Drilldown.To("/recurring", ("missedOnly", "true"), ("from", D(from)), ("to", D(to))), provider);
            }
            case QuestionIntent.ProjectBlockers when q.ProjectId is { } projectId:
            {
                var f = new TaskFilter { ProjectId = projectId, Blocked = true, Sort = "attention", PageSize = 50 };
                var page = await queries.QueryAsync(f, ct);
                var projectTasks = (await queries.FilterAsync(new TaskFilter { ProjectId = projectId, Open = true }, c, ct)).Select(t => t.Id);
                var external = await db.TaskDependencies.AsNoTracking()
                    .Where(d => projectTasks.Contains(d.TaskId) && !d.IsResolved && d.DependsOnTaskId == null)
                    .Select(d => d.ExternalDescription).Distinct().OrderBy(x => x).Take(5).ToListAsync(ct);
                var waitingOn = page.Items.Where(t => t.BlockedReason is not null).Select(t => $"{t.Number}: {t.BlockedReason}").Take(3).ToList();
                var name = scope.Project ?? "The project";
                var n = page.Total;
                var answer = n == 0 && external.Count == 0
                    ? $"{name} has no blockers right now."
                    : $"{name} has {n} blocked {TaskMetrics.Plural(n, "task")}"
                      + (external.Count > 0 ? $" and {external.Count} open external {TaskMetrics.Plural(external.Count, "dependency", "dependencies")} ({string.Join("; ", external.Where(x => x is not null))})" : "")
                      + (waitingOn.Count > 0 ? $" — {string.Join("; ", waitingOn)}" : "") + ".";
                return new AskAnswer(question, q, Interp($"Blockers in {name}"), answer, [.. page.Items], [], [], [], Drilldown.To($"/projects/{projectId}"), provider);
            }
            case QuestionIntent.ProjectBlockers:
                goto case QuestionIntent.Blocked;
            case QuestionIntent.Workload:
            {
                List<Guid> ids = q.EmployeeId is { } emp ? [emp] : actor.EmployeeId is { } me && q.TeamId is null ? [me] : [];
                var loads = await queries.WorkloadAsync(ids, q.TeamId, ct);
                var f = q.EmployeeId is { } e2 ? new TaskFilter { AssignedToId = e2, Open = true, Sort = "attention", PageSize = 50 }
                    : q.TeamId is { } t2 ? new TaskFilter { TeamId = t2, Open = true, Sort = "attention", PageSize = 50 }
                    : new TaskFilter { Scope = "mine", Open = true, Sort = "attention", PageSize = 50 };
                var page = await queries.QueryAsync(f, ct);
                string answer;
                if (loads.Count == 0) answer = "I could not tell whose workload you meant.";
                else if (q.TeamId is not null && loads.Count > 1)
                {
                    var heaviest = loads.OrderByDescending(l => l.OpenTasks + 2 * l.Overdue).First();
                    answer = $"{scope.Team ?? "The team"} has {loads.Sum(l => l.OpenTasks)} open task assignments across {loads.Count} people; the heaviest load is {heaviest.Name} ({heaviest.OpenTasks} open, {heaviest.Overdue} overdue — {heaviest.Load.ToLowerInvariant()}).";
                }
                else
                {
                    var l = loads[0];
                    answer = $"{l.Name} has {l.OpenTasks} open {TaskMetrics.Plural(l.OpenTasks, "task")} ({l.Overdue} overdue, {l.Critical} critical, {l.DueThisWeek} due this week) — workload is {l.Load.ToLowerInvariant()}.";
                }
                return new AskAnswer(question, q, Interp("Workload"), answer, [.. page.Items], [], [], [], TaskDrill(f), provider);
            }
            case QuestionIntent.TaskLookup:
            {
                var number = TaskNumbers.FindAll(q.TaskNumber ?? question).FirstOrDefault() ?? q.TaskNumber?.Trim().ToUpperInvariant();
                var vis = db.Tasks.AsNoTracking().VisibleTo(db, actor.Viewer).Where(x => x.Number == number);
                var items = await queries.ProjectAsync(vis, c, ct);
                if (items.Count == 0)
                    return new AskAnswer(question, q, Interp($"Look up {number}"), $"I could not find {number ?? "that task"}.", [], [], [], [], null, provider);
                var hit = items[0];
                var answer = $"{hit.Number} — {hit.Title}: {hit.DisplayStatus.ToLowerInvariant()}, {hit.DeadlineText.ToLowerInvariant()}, {hit.ProgressPercent}% done"
                    + (hit.PrimaryAssignee is { } a ? $", responsible: {a.Name}" : "") + (hit.IsBlocked && hit.BlockedReason is { } br ? $" (blocked: {br})" : "") + ".";
                return new AskAnswer(question, q, Interp($"Look up {hit.Number}"), answer, items, [], [], [], Drilldown.To($"/tasks/{hit.Id}"), provider);
            }
            case QuestionIntent.Summary:
            {
                var summary = await digest.ExecutiveSummaryAsync(ct);
                var answer = summary.Narrative ?? string.Join(" ", summary.Points.Take(5).Select(p => p.Text));
                return new AskAnswer(question, q, Interp("Executive summary"), answer, [], [], [], [], Drilldown.To("/"), provider);
            }
            default:
            {
                var text = string.IsNullOrWhiteSpace(q.SearchText) ? question : q.SearchText!;
                var f = Scoped(new TaskFilter { Text = text, Sort = "attention" });
                return await Tasks(f, n => n == 0
                        ? $"I could not interpret that question and found no tasks matching \"{text}\"."
                        : $"I could not interpret that question exactly; {n} {TaskMetrics.Plural(n, "task matches", "tasks match")} \"{text}\".",
                    $"Text search for \"{text}\"");
            }
        }
    }

    private static string Is(int n) => n == 1 ? "is" : "are";

    /// <summary>Names of the departments / teams / people / projects a question was narrowed to.</summary>
    private sealed record Scope(string? Department, string? Team, string? Employee, string? Project)
    {
        public string Noun(int n, string? adjective = null)
        {
            var count = n == 0 ? "No" : n.ToString(CultureInfo.InvariantCulture);
            var adj = adjective is null ? "" : adjective + " ";
            var group = Department ?? Team ?? Project;
            var core = $"{count} {adj}{(group is null ? "" : group + " ")}{TaskMetrics.Plural(n, "task")}";
            return Employee is null ? core : $"{core} assigned to {Employee}";
        }

        public string Describe()
        {
            var parts = new List<string>();
            if (Department is { } d) parts.Add($"in {d}");
            if (Team is { } t) parts.Add($"for {t}");
            if (Project is { } p) parts.Add($"in {p}");
            if (Employee is { } e) parts.Add($"assigned to {e}");
            return parts.Count == 0 ? "" : " " + string.Join(" ", parts);
        }

        public string DescribeTasks() => Department is { } d ? $" on {d} tasks" : Team is { } t ? $" on {t} tasks" : Project is { } p ? $" on {p}" : "";
    }

    /// <summary>Drill-down to the task list with query parameters named like <see cref="TaskFilter"/> fields.</summary>
    public static Drilldown TaskDrill(TaskFilter f)
    {
        var q = new List<(string, string)>();
        void Add(string key, object? value)
        {
            switch (value)
            {
                case null: return;
                case bool b: q.Add((key, b ? "true" : "false")); return;
                case DateOnly d: q.Add((key, D(d))); return;
                case string s when string.IsNullOrWhiteSpace(s): return;
                default: q.Add((key, Convert.ToString(value, CultureInfo.InvariantCulture)!)); return;
            }
        }
        Add("text", f.Text);
        Add("scope", f.Scope);
        if (f.Statuses is { Count: > 0 } st) Add("statuses", string.Join(",", st));
        Add("open", f.Open);
        Add("assignedToId", f.AssignedToId);
        Add("assignedById", f.AssignedById);
        Add("teamId", f.TeamId);
        Add("departmentId", f.DepartmentId);
        Add("projectId", f.ProjectId);
        if (f.Priorities is { Count: > 0 } pr) Add("priorities", string.Join(",", pr));
        if (f.Criticalities is { Count: > 0 } cr) Add("criticalities", string.Join(",", cr));
        Add("minCriticality", f.MinCriticality);
        if (f.Health is { Count: > 0 } h) Add("health", string.Join(",", h));
        Add("bucket", f.Bucket);
        Add("dueFrom", f.DueFrom);
        Add("dueTo", f.DueTo);
        Add("minDaysOverdue", f.MinDaysOverdue);
        Add("recurring", f.Recurring);
        Add("overdue", f.Overdue);
        Add("blocked", f.Blocked);
        Add("waitingForCeo", f.WaitingForCeo);
        Add("noResponse", f.NoResponse);
        Add("awaitingApproval", f.AwaitingApproval);
        Add("topLevelOnly", f.TopLevelOnly);
        Add("tag", f.Tag);
        Add("completedFrom", f.CompletedFrom);
        Add("completedTo", f.CompletedTo);
        Add("sort", f.Sort);
        return Drilldown.Tasks([.. q]);
    }

    // ═══════════════════════════════ Natural-language / voice delegation (spec §42, §48, §49) ═══════════════════════════════

    public async Task<ResolvedDraft> ParseCommandAsync(string text, bool voice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ValidationException("text_required", "Say or type what should be done.");
        var c = await queries.ClockAsync(ct);
        var dir = await DirectoryAsync(ct);
        TaskDraft draft;
        try { draft = await nl.ParseTaskCommandAsync(text.Trim(), dir, c.Now, c.Calendar, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Task command parsing failed ({Mode})", voice ? "voice" : "text");
            throw new ValidationException("parse_failed", "The command could not be understood. Please fill in the task form instead.");
        }

        var people = dir.People.ToDictionary(p => p.Id);
        PersonRef? P(Guid? id) => id is { } x && people.TryGetValue(x, out var p) ? new PersonRef(p.Id, p.DisplayName, p.JobTitle, p.Email) : null;
        var assigneeIds = new List<Guid>();
        if (draft.PrimaryAssigneeId is { } pa) assigneeIds.Add(pa);
        foreach (var id in draft.AssigneeIds) if (!assigneeIds.Contains(id)) assigneeIds.Add(id);
        var teams = draft.TeamIds.Select(id => dir.Teams.FirstOrDefault(t => t.Id == id)).Where(t => t is not null).Select(t => t!).ToList();
        var workloadIds = assigneeIds.Concat(teams.Where(t => t.LeaderId is not null).Select(t => t.LeaderId!.Value)).Distinct().ToList();
        var workload = await queries.WorkloadAsync(workloadIds, null, ct);

        string? dueText = null;
        if (draft.IsRecurring) dueText = draft.RecurrenceDue?.Describe();
        else if (draft.DueDate is { } due)
            dueText = $"Due {due.ToString("dddd d MMM yyyy", CultureInfo.InvariantCulture)}"
                + (draft.DueTime is { } tm ? $" at {tm.ToString("HH:mm", CultureInfo.InvariantCulture)}" : "")
                + $" ({DeadlineRules.Describe(due, draft.DueTime, WorkStatus.Assigned, c.Now, c.Calendar).ToLowerInvariant()})";

        return new ResolvedDraft(
            draft,
            P(draft.PrimaryAssigneeId),
            assigneeIds.Select(id => P(id)).Where(p => p is not null).Select(p => p!).ToList(),
            teams.Select(t => new NamedRef(t.Id, t.Name)).ToList(),
            draft.DepartmentId is { } d && dir.Departments.FirstOrDefault(x => x.Id == d) is { } dep ? new NamedRef(dep.Id, dep.Name) : null,
            draft.ProjectId is { } p && dir.Projects.FirstOrDefault(x => x.Id == p) is { } proj ? new NamedRef(proj.Id, proj.Name) : null,
            draft.FollowUp?.Describe(),
            draft.IsRecurring ? draft.Recurrence?.Describe() : null,
            dueText,
            workload,
            Provider());
    }
}
