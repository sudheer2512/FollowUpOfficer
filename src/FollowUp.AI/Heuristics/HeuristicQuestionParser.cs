using System.Globalization;
using System.Text.RegularExpressions;
using FollowUp.AI.Text;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Domain.Rules;

namespace FollowUp.AI.Heuristics;

/// <summary>
/// Deterministic parser that resolves an executive's natural-language question ("What has Development not responded
/// to?", "Show Finance tasks overdue by more than five days.") into a <see cref="QuestionQuery"/> Core can execute.
/// </summary>
/// <remarks>
/// Intent rules are evaluated in a fixed order (task number › summary › recurring missed › commitments › approvals ›
/// project blockers › no response › overdue › completed › workload › attention › due soon › critical › blocked); the
/// first that fires wins. Entities (departments, teams, people, projects) are resolved against the directory; a
/// single word naming both a department and a team ("Development") resolves to the department unless followed by
/// "team". Periods use the business calendar: weeks run Monday–Sunday. "More than N days" means at least N + 1.
/// Stateless and thread-safe.
/// </remarks>
public sealed partial class HeuristicQuestionParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Num = TextTools.NumberWordPattern;

    /// <summary>Parses <paramref name="question"/> relative to <paramref name="nowUtc"/>.</summary>
    public QuestionQuery Parse(string question, DirectorySnapshot directory, DateTime nowUtc, BusinessCalendar calendar)
    {
        var q = TextTools.Squash(question);
        if (q.Length == 0) return new QuestionQuery { Intent = QuestionIntent.Unknown, Confidence = 0, Interpretation = "Empty question." };

        var today = calendar.Today(nowUtc);
        var resolver = new DirectoryResolver(directory);
        var scope = ResolveScope(q, resolver, directory.CurrentUserId);
        var period = ParsePeriod(q, today);
        var minCriticality = BusinessCriticalRegex().IsMatch(q) ? Criticality.BusinessCritical
            : CriticalRegex().IsMatch(q) ? Criticality.Critical
            : ImportantRegex().IsMatch(q) ? Criticality.Important
            : (Criticality?)null;

        var query = new QuestionQuery
        {
            DepartmentId = scope.Department?.Id,
            TeamId = scope.Team?.Id,
            EmployeeId = scope.Person?.Id,
            ProjectId = scope.Project?.Id,
            From = period?.From,
            To = period?.To,
        };
        var who = scope.Label;
        var when = period is null ? "" : " " + period.Value.Label;

        // 1. Task number.
        if (TaskNumbers.FindAll(q) is { Count: > 0 } numbers)
            return query with { Intent = QuestionIntent.TaskLookup, TaskNumber = numbers[0], Confidence = 0.97, Interpretation = $"Status of {numbers[0]}" };

        // 2. Summary (overall, or of one project / person / team: "How is the e-invoicing project going?").
        if (SummaryRegex().IsMatch(q) || (who.Length > 0 && ScopedStatusRegex().IsMatch(q)))
            return query with { Intent = QuestionIntent.Summary, Confidence = 0.85, Interpretation = $"{(who.Length > 0 ? "Status summary for " + who : "Executive summary")}{when}" };

        // 3. Recurring activities missed.
        if (RecurringRegex().IsMatch(q) && MissedRegex().IsMatch(q))
            return query with { Intent = QuestionIntent.RecurringMissed, Confidence = 0.9, Interpretation = $"Recurring activities{Scope(who)} missed{when}" };

        // 4. Commitments / promises.
        if (CommitmentRegex().IsMatch(q))
        {
            var toMe = ToMeRegex().IsMatch(q);
            var madeTo = toMe ? " made to you" : "";
            if (MissedRegex().IsMatch(q))
                return query with { Intent = QuestionIntent.MissedCommitments, ToMe = toMe, Confidence = 0.9, Interpretation = $"Commitments{madeTo}{Scope(who)} that were missed{when}" };
            var due = period ?? (today, today.AddDays(7), "in the next 7 days");
            return query with
            {
                Intent = QuestionIntent.CommitmentsDue, ToMe = toMe, From = due.From, To = due.To, Confidence = 0.8,
                Interpretation = $"Commitments{madeTo}{Scope(who)} due {due.Label}",
            };
        }

        // 5. Approvals.
        if (ApprovalRegex().IsMatch(q))
            return query with { Intent = QuestionIntent.AwaitingApproval, Confidence = 0.9, Interpretation = $"Items awaiting your approval{Scope(who)}" };

        // 6. What is blocking a project.
        if (BlockingRegex().Match(q) is { Success: true } blocking)
        {
            if (scope.Project is not null)
                return query with { Intent = QuestionIntent.ProjectBlockers, Confidence = Math.Round(0.9 * Math.Max(0.8, scope.Project.Score), 2), Interpretation = $"What is blocking {scope.Project.Name}" };
            if (ProjectWordRegex().IsMatch(q))
            {
                var name = TextTools.Squash(ProjectWordRegex().Replace(q[(blocking.Index + blocking.Length)..], "")).Trim(' ', '?', '.', '!');
                name = LeadingArticleRegex().Replace(name, "");
                return query with { Intent = QuestionIntent.ProjectBlockers, SearchText = name.Length > 0 ? name : null, Confidence = 0.55, Interpretation = $"What is blocking the project \"{name}\" (project not found — searching by name)" };
            }
            return query with { Intent = QuestionIntent.Blocked, Confidence = 0.75, Interpretation = $"Blocked{Prefix(who)} tasks" };
        }

        // 7. No response.
        if (NoResponseRegex().IsMatch(q))
            return query with { Intent = QuestionIntent.NoResponse, Confidence = 0.9, Interpretation = who.Length > 0 ? $"Tasks {who} has not responded to{when}" : $"Tasks with no response{when}" };

        // 8. Overdue.
        if (OverdueRegex().IsMatch(q))
        {
            var minDays = ParseMinDaysOverdue(q, out var strictlyMore);
            var late = minDays is { } n
                ? strictlyMore ? $", more than {n - 1} day{(n - 1 == 1 ? "" : "s")} late" : $", at least {n} day{(n == 1 ? "" : "s")} late"
                : "";
            return query with
            {
                Intent = QuestionIntent.Overdue, MinDaysOverdue = minDays, MinCriticality = minCriticality, Confidence = 0.9,
                Interpretation = $"Overdue{Prefix(who)} tasks{late}{when}",
            };
        }

        // 9. Completed recently.
        if (CompletedRegex().Match(q) is { Success: true } done && !NotBeforeRegex().IsMatch(q[..done.Index]))
        {
            var p = period ?? (today.AddDays(-6), today, "in the last 7 days");
            return query with
            {
                Intent = QuestionIntent.CompletedRecently, From = p.From, To = p.To, Confidence = 0.85,
                Interpretation = who.Length > 0 ? $"Work {who} completed {p.Label}" : $"Work completed {p.Label}",
            };
        }

        // 10. Workload.
        if (WorkloadRegex().IsMatch(q))
            return query with { Intent = QuestionIntent.Workload, Confidence = who.Length > 0 ? 0.88 : 0.7, Interpretation = who.Length > 0 ? $"Workload of {who}" : "Workload across the organisation" };

        // 11. Needs my attention.
        if (AttentionRegex().IsMatch(q))
            return query with
            {
                Intent = QuestionIntent.AttentionRequired, MinCriticality = minCriticality, Confidence = 0.9,
                Interpretation = $"{(minCriticality is { } c ? TextTools.Capitalize(Describe(c)) + " work" : "Work")}{Scope(who)} that needs your attention{when}",
            };

        // 12. Due soon.
        if (DueRegex().IsMatch(q))
        {
            var p = period ?? (today, today.AddDays(7), "in the next 7 days");
            return query with
            {
                Intent = QuestionIntent.DueSoon, From = p.From, To = p.To, MinCriticality = minCriticality, Confidence = 0.88,
                Interpretation = $"{TextTools.Capitalize((Prefix(who) + " tasks").Trim())} due {p.Label}",
            };
        }

        // 13. Critical work.
        if (minCriticality is { } crit && crit >= Criticality.Critical)
            return query with { Intent = QuestionIntent.Critical, MinCriticality = crit, Confidence = 0.8, Interpretation = $"Open {Describe(crit)}{Prefix(who)} tasks" };

        // 14. Blocked.
        if (BlockedRegex().IsMatch(q))
            return query with { Intent = QuestionIntent.Blocked, Confidence = 0.88, Interpretation = $"Blocked{Prefix(who)} tasks{when}" };

        // 15. A person or team with "tasks" / "work" → their workload.
        if (who.Length > 0 && TasksWordRegex().IsMatch(q))
            return query with { Intent = QuestionIntent.Workload, Confidence = 0.6, Interpretation = $"Tasks of {who}" };

        var search = string.Join(' ', TextTools.Words(q).Where(w => !TextTools.StopWords.Contains(w) && !QuestionWordRegex().IsMatch(w)));
        return query with
        {
            Intent = QuestionIntent.Unknown, SearchText = search.Length > 0 ? search : q, Confidence = 0.2,
            Interpretation = $"Not sure what you are asking — searching for \"{(search.Length > 0 ? search : q)}\"",
        };

        static string Scope(string w) => w.Length > 0 ? " for " + w : "";
        static string Prefix(string w) => w.Length > 0 ? " " + w : "";
    }

    private static string Describe(Criticality c) => c switch
    {
        Criticality.BusinessCritical => "business-critical",
        Criticality.Critical => "critical",
        Criticality.Important => "important",
        _ => c.ToString().ToLowerInvariant(),
    };

    // ───────────────────────────── scope (who / which project) ─────────────────────────────

    private sealed record ScopeResult(ResolvedEntity? Department, ResolvedEntity? Team, ResolvedEntity? Person, ResolvedEntity? Project)
    {
        public string Label => Project?.Name ?? Person?.Name ?? Team?.Name ?? Department?.Name ?? "";
    }

    private static ScopeResult ResolveScope(string q, DirectoryResolver resolver, Guid? currentUserId)
    {
        ResolvedEntity? dept = null, team = null, person = null, project = null;
        var mentions = resolver.FindMentions(q);
        foreach (var group in mentions.GroupBy(m => (m.Index, m.Length)))
        {
            var list = group.ToList();
            var followedByTeam = TeamAfterRegex().IsMatch(q[(group.Key.Index + group.Key.Length)..]);
            var chosen = list.Count == 1 ? list[0]
                : list.FirstOrDefault(m => m.Entity.Kind == (followedByTeam ? EntityKind.Team : EntityKind.Department))
                  ?? list.OrderByDescending(m => m.Entity.Score).First();
            var e = chosen.Entity;
            switch (e.Kind)
            {
                case EntityKind.Department: dept ??= e; break;
                case EntityKind.Team: team ??= e; break;
                case EntityKind.Person when e.Id != currentUserId: person ??= e; break;
                case EntityKind.Project: project ??= e; break;
            }
        }
        return new ScopeResult(dept, team, person, project);
    }

    // ───────────────────────────── periods ─────────────────────────────

    private static (DateOnly From, DateOnly To, string Label)? ParsePeriod(string q, DateOnly today)
    {
        var m = PeriodRegex().Match(q);
        if (!m.Success) return null;
        var p = TextTools.Squash(m.Groups["p"].Value).ToLowerInvariant();
        var week = BusinessCalendar.StartOfWeek(today);
        var month = new DateOnly(today.Year, today.Month, 1);
        (DateOnly, DateOnly) range;
        string label;
        if (m.Groups["n"].Success && TextTools.ParseNumber(m.Groups["n"].Value) is { } n && n > 0)
        {
            var days = m.Groups["u"].Value.StartsWith("week", StringComparison.OrdinalIgnoreCase) ? n * 7 : n;
            range = (today.AddDays(-(days - 1)), today);
            label = $"in the last {days} days";
            return (range.Item1, range.Item2, label);
        }
        switch (p)
        {
            case "today": range = (today, today); label = "today"; break;
            case "tomorrow": range = (today.AddDays(1), today.AddDays(1)); label = "tomorrow"; break;
            case "yesterday": range = (today.AddDays(-1), today.AddDays(-1)); label = "yesterday"; break;
            case "this week": range = (week, week.AddDays(6)); label = "this week"; break;
            case "last week" or "previous week": range = (week.AddDays(-7), week.AddDays(-1)); label = "last week"; break;
            case "next week": range = (week.AddDays(7), week.AddDays(13)); label = "next week"; break;
            case "past week": range = (today.AddDays(-6), today); label = "in the past week"; break;
            case "this month": range = (month, month.AddMonths(1).AddDays(-1)); label = "this month"; break;
            case "last month" or "previous month": range = (month.AddMonths(-1), month.AddDays(-1)); label = "last month"; break;
            case "next month": range = (month.AddMonths(1), month.AddMonths(2).AddDays(-1)); label = "next month"; break;
            case "this quarter":
            {
                var qs = new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
                range = (qs, qs.AddMonths(3).AddDays(-1)); label = "this quarter"; break;
            }
            case "last quarter":
            {
                var qs = new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1).AddMonths(-3);
                range = (qs, qs.AddMonths(3).AddDays(-1)); label = "last quarter"; break;
            }
            case "this year": range = (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)); label = "this year"; break;
            default: range = (today.AddDays(-6), today); label = "recently"; break;
        }
        var (from, to) = range;
        var span = from == to
            ? from.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : $"{from.ToString("d MMM", CultureInfo.InvariantCulture)} – {to.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
        return (from, to, $"{label} ({span})");
    }

    private static int? ParseMinDaysOverdue(string q, out bool strictlyMore)
    {
        strictlyMore = false;
        int Days(Match m) => (TextTools.ParseNumber(m.Groups["n"].Value) ?? 0) * (m.Groups["u"].Value.StartsWith("week", StringComparison.OrdinalIgnoreCase) ? 7 : 1);

        if (MoreThanDaysRegex().Match(q) is { Success: true } more && Days(more) > 0)
        {
            strictlyMore = true;
            return Days(more) + 1;
        }
        if (AtLeastDaysRegex().Match(q) is { Success: true } least && Days(least) > 0) return Days(least);
        if (ByDaysRegex().Match(q) is { Success: true } by && Days(by) > 0) return Days(by);
        return null;
    }

    // ───────────────────────────── patterns ─────────────────────────────

    [GeneratedRegex(@"\b(?:summary|summari[sz]e|overview|how\s+(?:are|is)\s+(?:we|everything|things|it|the\s+company|the\s+business)\s+(?:doing|going)|how're\s+we\s+doing|big\s+picture|brief\s+me|briefing|status\s+report|overall\s+status|where\s+do\s+we\s+stand)\b", Opts)]
    private static partial Regex SummaryRegex();

    [GeneratedRegex(@"\bhow\s+(?:is|are|'s)\b.{1,80}?\b(?:going|doing|progressing|coming\s+along|shaping\s+up)\b|\b(?:status|progress|update)\s+(?:of|on)\b|\bwhere\s+(?:is|are)\b.{1,60}?\bat\b", Opts)]
    private static partial Regex ScopedStatusRegex();

    [GeneratedRegex(@"\b(?:recurring|routine|periodic|repeating|repeated|scheduled\s+activit(?:y|ies)|regular\s+activit(?:y|ies))\b", Opts)]
    private static partial Regex RecurringRegex();

    [GeneratedRegex(@"\b(?:miss(?:ed|ing)?|broken|broke|not\s+kept|failed|overdue|slipped|lapsed|unmet|not\s+met|skipped|not\s+(?:done|completed|created))\b", Opts)]
    private static partial Regex MissedRegex();

    [GeneratedRegex(@"\b(?:promise[sd]?|commitments?|committed|commit)\b", Opts)]
    private static partial Regex CommitmentRegex();

    [GeneratedRegex(@"\b(?:(?:made|given)\s+to\s+me|promised\s+me|to\s+me|my\s+commitments|gave\s+me)\b", Opts)]
    private static partial Regex ToMeRegex();

    [GeneratedRegex(@"\b(?:awaiting|pending|waiting\s+(?:for|on))\s+(?:my\s+)?(?:approval|sign[\s-]?off)|\b(?:need|needs|require|requires|requiring)\s+my\s+(?:approval|sign[\s-]?off)|\bfor\s+my\s+approval\b|\bapprovals?\s+(?:pending|waiting)|\bwhat\s+(?:do\s+)?i\s+(?:need\s+to|have\s+to|must)\s+approve\b|\bpending\s+approvals?\b", Opts)]
    private static partial Regex ApprovalRegex();

    [GeneratedRegex(@"\b(?:blocking|holding\s+up|holding\s+back|stopping|blockers?\s+(?:on|for|in)|impediments?\s+(?:on|for|in)|stuck\s+(?:on|in))\b", Opts)]
    private static partial Regex BlockingRegex();

    [GeneratedRegex(@"\b(?:project|programme|program|initiative)\b", Opts)]
    private static partial Regex ProjectWordRegex();

    [GeneratedRegex(@"(?:\bnot|n't|n’t|\bnever)\s+(?:yet\s+)?(?:responded|replied|answered|acknowledged|reacted|got\s+back|respond(?:ed)?|reply|acknowledge[d]?)\b|\b(?:no\s+(?:response|reply|replies|answer|acknowledg(?:e)?ments?|updates?)|unanswered|without\s+(?:a\s+)?(?:response|reply)|ignor(?:ed|ing)|not\s+responding|unresponsive|gone\s+silent|(?:haven't|have\s+not|hasn't|has\s+not)\s+heard|not\s+heard\s+back)\b", Opts)]
    private static partial Regex NoResponseRegex();

    [GeneratedRegex(@"\b(?:overdue|past\s+(?:the\s+)?due(?:\s+date)?|late|behind\s+schedule|delayed|missed\s+(?:the\s+|their\s+)?deadlines?|beyond\s+(?:the\s+)?deadline|slipped)\b", Opts)]
    private static partial Regex OverdueRegex();

    [GeneratedRegex(@"\b(?:by\s+)?(?:more\s+than|over|above|greater\s+than|beyond|longer\s+than|exceeding)\s+(?<n>" + Num + @")\s+(?:working\s+|business\s+)?(?<u>days?|weeks?)\b", Opts)]
    private static partial Regex MoreThanDaysRegex();

    [GeneratedRegex(@"\b(?:by\s+)?(?:at\s+least|minimum(?:\s+of)?|min\.?|no\s+less\s+than)\s+(?<n>" + Num + @")\s+(?:working\s+|business\s+)?(?<u>days?|weeks?)\b|\b(?<n>\d{1,3})\s*\+\s*(?<u>days?|weeks?)\b|\b(?<n>" + Num + @")\s+(?:or\s+more|plus)\s+(?<u>days?|weeks?)\b", Opts)]
    private static partial Regex AtLeastDaysRegex();

    [GeneratedRegex(@"\bby\s+(?<n>" + Num + @")\s+(?:working\s+|business\s+)?(?<u>days?|weeks?)\b", Opts)]
    private static partial Regex ByDaysRegex();

    [GeneratedRegex(@"\b(?:complete[ds]?|finish(?:ed)?|done|close[ds]?|deliver(?:ed)?|accomplished|achieved|wrapped\s+up)\b", Opts)]
    private static partial Regex CompletedRegex();

    [GeneratedRegex(@"(?:\bnot|n't|n’t|\byet\s+to\s+be)\s+(?:\w+\s+){0,2}$", Opts)]
    private static partial Regex NotBeforeRegex();

    [GeneratedRegex(@"\b(?:workload|work\s+load|how\s+busy|capacity|bandwidth|how\s+much\s+(?:work|is\s+on)|working\s+on|on\s+(?:\w+(?:'s|’s)?\s+)?plate|load\s+on|assigned\s+to)\b", Opts)]
    private static partial Regex WorkloadRegex();

    [GeneratedRegex(@"\b(?:attention|requires?\s+(?:me|my)|needs?\s+(?:me|my)|my\s+(?:input|intervention|decision|action|involvement)|should\s+i\s+(?:look|worry|focus|know)|focus\s+on|escalat\w+\s+to\s+me|for\s+me\s+to\s+(?:act|decide|look))\b", Opts)]
    private static partial Regex AttentionRegex();

    [GeneratedRegex(@"\b(?:due|deadlines?|coming\s+up|upcoming|falls?\s+due)\b", Opts)]
    private static partial Regex DueRegex();

    [GeneratedRegex(@"\b(?:business|mission)[\s-]critical\b", Opts)]
    private static partial Regex BusinessCriticalRegex();

    [GeneratedRegex(@"(?<!\b(?:business|mission)[\s-])\bcritical\b", Opts)]
    private static partial Regex CriticalRegex();

    [GeneratedRegex(@"\b(?:important|high[\s-]priority|top\s+priority)\b", Opts)]
    private static partial Regex ImportantRegex();

    [GeneratedRegex(@"\b(?:blocked|stuck|blockers?|impediments?|on\s+hold|waiting\s+(?:for|on)|held\s+up|dependenc(?:y|ies))\b", Opts)]
    private static partial Regex BlockedRegex();

    [GeneratedRegex(@"\b(?:tasks?|work|items?|activities)\b", Opts)]
    private static partial Regex TasksWordRegex();

    [GeneratedRegex(@"^(?:what|which|who|whom|whose|when|where|why|how|show|list|give|tell|find|me|are|is|has|have|do|does|did)$", Opts)]
    private static partial Regex QuestionWordRegex();

    [GeneratedRegex(@"^\s*(?:team|squad)\b", Opts)]
    private static partial Regex TeamAfterRegex();

    [GeneratedRegex(@"^(?:the|a|an)\s+", Opts)]
    private static partial Regex LeadingArticleRegex();

    [GeneratedRegex(@"\b(?<p>today|tomorrow|yesterday|this\s+week|last\s+week|previous\s+week|past\s+week|next\s+week|this\s+month|last\s+month|previous\s+month|next\s+month|this\s+quarter|last\s+quarter|this\s+year|recently|lately|(?:in\s+the\s+)?(?:last|past)\s+(?<n>" + Num + @")\s+(?<u>days|weeks))\b", Opts)]
    private static partial Regex PeriodRegex();
}
