using System.Globalization;
using System.Text.RegularExpressions;
using FollowUp.AI.Dates;
using FollowUp.AI.Text;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Domain.Rules;

namespace FollowUp.AI.Heuristics;

/// <summary>
/// Deterministic parser that turns a natural-language or voice command into a <see cref="TaskDraft"/>:
/// assignees (people, teams, departments), title, due date / time, follow-up schedule, priority, criticality,
/// "tell me if it gets delayed", approval, recurrence and the recurrence due rule — with a note per extracted field.
/// </summary>
/// <remarks>
/// <para>Phrases that configure the task (follow-up, notifications, approval, recurrence, due rule, priority flags and the
/// chosen dates) are recognised first and blanked out; the title is the remaining imperative clause of the core sentence
/// ("Ask X to &lt;action&gt;", "Create a task for X to &lt;action&gt;", "X should &lt;action&gt;", "X: &lt;action&gt;").</para>
/// <para>Criticality is raised to <see cref="Criticality.Important"/> when the CEO asks to be told about delays.
/// <see cref="TaskDraft.NeedsConfirmation"/> stays true unless every name resolved, an assignee and a due date (or
/// recurrence) were found and the overall confidence is at least 0.85.</para>
/// Stateless and thread-safe.
/// </remarks>
public sealed partial class HeuristicCommandParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Day = "monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tues|tue|wed|thurs|thur|thu|fri|sat|sun";
    private const string DayList = "(?:" + Day + @")s?(?:\s*(?:,|and|&)\s*(?:" + Day + ")s?)*";
    private const string Num = TextTools.NumberWordPattern;
    private const string Dom = @"3[01]|[12]\d|0?[1-9]";

    /// <summary>Default local time at which recurring occurrences are created.</summary>
    public static readonly TimeOnly DefaultCreateTime = new(8, 0);

    /// <summary>Parses <paramref name="command"/> against the directory, relative to <paramref name="nowUtc"/>.</summary>
    public TaskDraft Parse(string command, DirectorySnapshot directory, DateTime nowUtc, BusinessCalendar calendar)
    {
        var original = TextTools.Squash(command);
        if (original.Length == 0)
            return new TaskDraft { Confidence = 0, NeedsConfirmation = true, Notes = [new DraftNote("Command", "The command is empty.")] };

        var resolver = new DirectoryResolver(directory);
        var notes = new List<DraftNote>();
        var unresolved = new List<string>();
        var mask = new Masker(original);
        var confidence = 0.95;

        // ── flags ──
        var notify = NotifyRegex().Match(mask.Text);
        if (notify.Success)
        {
            notes.Add(new DraftNote("NotifyCeoOnDelay", $"\"{Clean(notify.Value)}\" → you will be alerted if it is delayed."));
            mask.Mask(notify);
        }

        var approval = ApprovalRegex().Match(mask.Text);
        if (approval.Success)
        {
            notes.Add(new DraftNote("RequiresApproval", $"\"{Clean(approval.Value)}\" → completion needs your approval."));
            mask.Mask(approval);
        }

        var priority = TaskPriority.Normal;
        if (LowPriorityRegex().Match(original) is { Success: true } low) { priority = TaskPriority.Low; notes.Add(new DraftNote("Priority", $"Low (\"{low.Value}\").")); }
        else if (UrgentRegex().Match(original) is { Success: true } urg) { priority = TaskPriority.Urgent; notes.Add(new DraftNote("Priority", $"Urgent (\"{urg.Value}\").")); }
        else if (HighPriorityRegex().Match(original) is { Success: true } high) { priority = TaskPriority.High; notes.Add(new DraftNote("Priority", $"High (\"{high.Value}\").")); }

        var criticality = Criticality.Normal;
        if (BusinessCriticalRegex().Match(original) is { Success: true } bc) { criticality = Criticality.BusinessCritical; notes.Add(new DraftNote("Criticality", $"Business critical (\"{bc.Value}\").")); }
        else if (CriticalRegex().Match(original) is { Success: true } cr) { criticality = Criticality.Critical; notes.Add(new DraftNote("Criticality", $"Critical (\"{cr.Value}\").")); }
        if (notify.Success && criticality < Criticality.Important)
        {
            criticality = Criticality.Important;
            notes.Add(new DraftNote("Criticality", "Important because you asked to be told if it gets delayed."));
        }
        foreach (Match m in FlagPhraseRegex().Matches(mask.Text)) mask.Mask(m);
        foreach (Match m in FlagPrefixRegex().Matches(mask.Text)) mask.Mask(m);

        // ── follow-up ──
        var followUp = ParseFollowUp(mask, calendar, notes);

        // ── recurrence ──
        var recurringWord = RecurringWordRegex().IsMatch(original);
        // Bare adjectives ("the monthly report") only count as recurrence when the command says "recurring".
        var recurrence = ParseRecurrence(mask, guardAdjectives: true) ?? (recurringWord ? ParseRecurrence(mask, guardAdjectives: false) : null);
        DueRule? recurrenceDue = null;
        var isRecurring = recurrence is not null || recurringWord;
        if (isRecurring)
        {
            if (recurrence is null)
            {
                recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, CreateTime = DefaultCreateTime };
                confidence -= 0.2;
                notes.Add(new DraftNote("Recurrence", "Recurring, but no frequency was found — assumed weekly on the start day. Please confirm."));
            }
            else notes.Add(new DraftNote("Recurrence", recurrence.Describe() + "."));
            recurrenceDue = ParseDueRule(mask, calendar);
            notes.Add(new DraftNote("RecurrenceDue", TextTools.Capitalize(recurrenceDue.Describe()) + "."));
        }

        // ── project ──
        Guid? projectId = null;
        foreach (var projectMention in resolver.FindMentions(original).Where(m => m.Entity.Kind == EntityKind.Project))
        {
            if (projectId is null)
            {
                projectId = projectMention.Entity.Id;
                notes.Add(new DraftNote("Project", $"\"{projectMention.Text}\" → {projectMention.Entity.Name} ({projectMention.Entity.How})."));
            }
            if (projectMention.Entity.Id != projectId) continue;
            // "… for the eInvoice project" describes the task's project, not its action: drop it from the title.
            var at = projectMention.Index;
            var lead = ProjectLeadRegex().Match(mask.Text[..at]);
            if (lead.Success)
            {
                var tail = ProjectTailRegex().Match(mask.Text[(at + projectMention.Length)..]);
                mask.Mask(lead.Index, at + projectMention.Length - lead.Index + (tail.Success ? tail.Length : 0));
            }
        }

        // ── dates (one-off tasks) ──
        DateOnly? dueDate = null, startDate = null;
        TimeOnly? dueTime = null;
        foreach (var d in DateExpressionParser.Parse(mask.Text, nowUtc, calendar))
        {
            var before = mask.Text[Math.Max(0, d.Index - 30)..d.Index];
            if (StartPrepositionRegex().Match(before) is { Success: true } sp)
            {
                startDate ??= d.Date;
                notes.Add(new DraftNote("StartDate", $"\"{Clean(sp.Value + d.Text)}\" → {TextTools.Show(d.Date)}."));
                mask.Mask(d.Index - sp.Length, sp.Length + d.Length);
                continue;
            }
            if (isRecurring) continue;
            var dp = DuePrepositionRegex().Match(before);
            if (dueDate is null || dp.Success)
            {
                if (dueDate is not null && !dp.Success) continue;
                dueDate = d.Date;
                dueTime = d.Time;
                var lead = dp.Success ? dp.Length : 0;
                notes.RemoveAll(n => n.Field == "DueDate");
                notes.Add(new DraftNote("DueDate", $"\"{Clean((dp.Success ? dp.Value : "") + d.Text)}\" → {TextTools.Show(d.Date)}{(d.Time is { } t ? " " + TextTools.Show(t) : "")}."));
                mask.Mask(d.Index - lead, lead + d.Length);
                if (dp.Success) break;
            }
        }
        if (!isRecurring && dueDate is null)
        {
            confidence -= 0.1;
            notes.Add(new DraftNote("DueDate", "No due date found — please set one."));
        }

        // ── assignees and title ──
        var (peopleChunk, action, fromPattern) = SplitCommand(mask.Text, resolver);
        if (!fromPattern) confidence -= 0.15;

        var persons = new List<ResolvedEntity>();
        var teams = new List<ResolvedEntity>();
        var departments = new List<ResolvedEntity>();
        if (peopleChunk is not null)
            ResolvePeople(peopleChunk, resolver, directory.CurrentUserId, persons, teams, departments, unresolved, notes, ref confidence);
        if (persons.Count + teams.Count + departments.Count == 0 && peopleChunk is null)
        {
            foreach (var m in resolver.FindMentions(mask.Text).Where(m => m.Entity.Kind is EntityKind.Person or EntityKind.Team))
            {
                if (m.Entity.Id == directory.CurrentUserId) continue;
                (m.Entity.Kind == EntityKind.Person ? persons : teams).Add(m.Entity);
                notes.Add(new DraftNote(m.Entity.Kind == EntityKind.Person ? "Assignee" : "Team", $"\"{m.Text}\" → {m.Entity.Name} (found in the text, {m.Entity.How})."));
                confidence -= 0.05;
            }
        }

        Guid? primary = persons.FirstOrDefault()?.Id;
        if (primary is null && teams.FirstOrDefault()?.LeaderId is { } leader)
        {
            primary = leader;
            var leaderName = directory.People.FirstOrDefault(p => p.Id == leader)?.DisplayName ?? "the team leader";
            notes.Add(new DraftNote("Assignee", $"No person named — the team leader {leaderName} is the primary assignee."));
        }
        if (primary is null)
        {
            confidence -= 0.3;
            notes.Add(new DraftNote("Assignee", "No assignee could be identified — please choose one."));
        }

        var departmentId = departments.FirstOrDefault()?.Id ?? teams.FirstOrDefault()?.DepartmentId ?? persons.FirstOrDefault()?.DepartmentId;

        var title = BuildTitle(action);
        if (title.Length == 0)
        {
            title = TextTools.Truncate(original, 120);
            confidence -= 0.2;
            notes.Add(new DraftNote("Title", "Could not isolate the action — the whole command is used as the title."));
        }
        else notes.Insert(0, new DraftNote("Title", $"\"{title}\" (the action in the command)."));

        confidence = Math.Round(Math.Clamp(confidence, 0.05, 0.99), 2);
        var needsConfirmation = confidence < 0.85 || unresolved.Count > 0 || primary is null || (!isRecurring && dueDate is null);

        return new TaskDraft
        {
            Title = title,
            Description = original,
            PrimaryAssigneeId = primary,
            AssigneeIds = persons.Select(p => p.Id).Distinct().ToList(),
            TeamIds = teams.Select(t => t.Id).Distinct().ToList(),
            DepartmentId = departmentId,
            ProjectId = projectId,
            StartDate = startDate,
            DueDate = isRecurring ? null : dueDate,
            DueTime = isRecurring ? null : dueTime,
            FollowUp = followUp,
            Priority = priority,
            Criticality = criticality,
            NotifyCeoOnDelay = notify.Success,
            RequiresApproval = approval.Success,
            IsRecurring = isRecurring,
            Recurrence = recurrence,
            RecurrenceDue = recurrenceDue,
            Confidence = confidence,
            Notes = notes,
            UnresolvedNames = unresolved,
            NeedsConfirmation = needsConfirmation,
        };
    }

    // ───────────────────────────── follow-up ─────────────────────────────

    private static FollowUpSchedule? ParseFollowUp(Masker mask, BusinessCalendar calendar, List<DraftNote> notes)
    {
        if (NoFollowUpRegex().Match(mask.Text) is { Success: true } none)
        {
            mask.Mask(none);
            notes.Add(new DraftNote("FollowUp", $"\"{Clean(none.Value)}\" → no automatic follow-up."));
            return FollowUpSchedule.NoneSchedule;
        }

        var m = FollowUpRegex().Match(mask.Text);
        if (!m.Success) m = FollowUpAdjectiveRegex().Match(mask.Text);
        if (!m.Success) return null;

        var f = TextTools.Squash(m.Groups["freq"].Value).ToLowerInvariant();
        FollowUpSchedule s;
        if (m.Groups["days"].Success)
        {
            var days = ParseDays(m.Groups["days"].Value);
            s = days.Count == 1 ? FollowUpSchedule.Weekly(days[0]) : FollowUpSchedule.OnWeekdays([.. days]);
        }
        else if (m.Groups["n"].Success && TextTools.ParseNumber(m.Groups["n"].Value) is { } n)
            s = n == 1 ? FollowUpSchedule.Daily() : FollowUpSchedule.EveryDays(n);
        else if (f.Contains("other day", StringComparison.Ordinal) || f.Contains("alternate", StringComparison.Ordinal))
            s = FollowUpSchedule.EveryDays(2);
        else if (f.StartsWith("twice", StringComparison.Ordinal))
            s = FollowUpSchedule.OnWeekdays(DayOfWeek.Monday, DayOfWeek.Thursday);
        else if (f.Contains("fortnight", StringComparison.Ordinal) || f.Contains("two weeks", StringComparison.Ordinal) || f.Contains("2 weeks", StringComparison.Ordinal) || f.Contains("other week", StringComparison.Ordinal))
            s = new FollowUpSchedule { Frequency = FollowUpFrequency.Fortnightly };
        else if (f.Contains("week", StringComparison.Ordinal))
            s = new FollowUpSchedule { Frequency = FollowUpFrequency.Weekly };
        else if (f.Contains("month", StringComparison.Ordinal))
            s = new FollowUpSchedule { Frequency = FollowUpFrequency.Monthly };
        else
            s = FollowUpSchedule.Daily();

        if (m.Groups["time"].Success && ParseClock(m.Groups["time"].Value, calendar) is { } at) s = s with { TimeOfDay = at };
        mask.Mask(m);
        notes.Add(new DraftNote("FollowUp", $"\"{Clean(m.Value)}\" → {s.Describe()}."));
        return s;
    }

    // ───────────────────────────── recurrence ─────────────────────────────

    private static RecurrenceRule? ParseRecurrence(Masker mask, bool guardAdjectives)
    {
        var text = mask.Text;
        RecurrenceRule? rule = null;
        Match? used = null;

        bool Try(Regex regex, Func<Match, RecurrenceRule?> build, bool adjectiveGuard = false)
        {
            foreach (Match m in regex.Matches(text))
            {
                if (DuePrefixRegex().IsMatch(text[Math.Max(0, m.Index - 12)..m.Index])) continue; // "due by the 5th of the month" is a due rule
                if (adjectiveGuard && guardAdjectives && IsAdjectiveUse(text, m)) continue;
                var r = build(m);
                if (r is null) continue;
                rule = r;
                used = m;
                return true;
            }
            return false;
        }

        _ = Try(NthOfMonthRegex(), m => new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, MonthlyPattern = MonthlyPattern.DayOfMonth, DayOfMonth = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture) })
            || Try(SpecialDayOfMonthRegex(), m =>
            {
                var first = m.Groups["which"].Value.Equals("first", StringComparison.OrdinalIgnoreCase);
                var working = m.Groups["working"].Success;
                var pattern = m.Groups["monthend"].Success ? MonthlyPattern.LastDay
                    : first ? (working ? MonthlyPattern.FirstWorkingDay : MonthlyPattern.DayOfMonth)
                    : working ? MonthlyPattern.LastWorkingDay : MonthlyPattern.LastDay;
                return new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, MonthlyPattern = pattern, DayOfMonth = 1 };
            })
            || Try(FinancialYearRegex(), _ => new RecurrenceRule { Frequency = RecurrenceFrequency.FinancialYear })
            || Try(WorkingDaysRegex(), _ => new RecurrenceRule { Frequency = RecurrenceFrequency.WorkingDays })
            || Try(EveryWeekdaysRegex(), m => new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, DaysOfWeek = ParseDays(m.Groups["days"].Value) })
            || Try(FortnightlyRegex(), m => new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, Interval = 2, DaysOfWeek = m.Groups["day"].Success ? ParseDays(m.Groups["day"].Value) : [] }, adjectiveGuard: true)
            || Try(EveryNRegex(), m =>
            {
                if (TextTools.ParseNumber(m.Groups["n"].Value) is not { } n || n < 1) return null;
                var u = m.Groups["u"].Value.ToLowerInvariant();
                return u.StartsWith("day", StringComparison.Ordinal) ? new RecurrenceRule { Frequency = RecurrenceFrequency.Daily, Interval = n }
                    : u.StartsWith("week", StringComparison.Ordinal) ? new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, Interval = n }
                    : u.StartsWith("month", StringComparison.Ordinal) ? n switch
                    {
                        3 => new RecurrenceRule { Frequency = RecurrenceFrequency.Quarterly },
                        6 => new RecurrenceRule { Frequency = RecurrenceFrequency.HalfYearly },
                        12 => new RecurrenceRule { Frequency = RecurrenceFrequency.Yearly },
                        _ => new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, Interval = n },
                    }
                    : new RecurrenceRule { Frequency = RecurrenceFrequency.Yearly, Interval = n };
            })
            || Try(WeeklyRegex(), m => new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, DaysOfWeek = m.Groups["day"].Success ? ParseDays(m.Groups["day"].Value) : [] }, adjectiveGuard: true)
            || Try(QuarterlyRegex(), _ => new RecurrenceRule { Frequency = RecurrenceFrequency.Quarterly }, adjectiveGuard: true)
            || Try(HalfYearlyRegex(), _ => new RecurrenceRule { Frequency = RecurrenceFrequency.HalfYearly }, adjectiveGuard: true)
            || Try(YearlyRegex(), _ => new RecurrenceRule { Frequency = RecurrenceFrequency.Yearly }, adjectiveGuard: true)
            || Try(MonthlyRegex(), m => new RecurrenceRule
            {
                Frequency = RecurrenceFrequency.Monthly,
                DayOfMonth = m.Groups["d"].Success ? int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture) : 1,
            }, adjectiveGuard: true)
            || Try(DailyRegex(), _ => new RecurrenceRule { Frequency = RecurrenceFrequency.Daily }, adjectiveGuard: true);

        if (rule is null || used is null) return null;
        mask.Mask(used);

        // "every Monday morning" / "every Monday at 9 am": the creation time of each occurrence.
        var after = mask.Text[(used.Index + used.Length)..];
        var ct = CreateTimeRegex().Match(after);
        var createTime = DefaultCreateTime;
        if (ct.Success)
        {
            if (ct.Groups["t"].Success && ParseClock(ct.Groups["t"].Value, BusinessCalendar.Default) is { } t) createTime = t;
            mask.Mask(used.Index + used.Length + ct.Index, ct.Length);
        }
        return rule with { CreateTime = createTime };
    }

    private static DueRule ParseDueRule(Masker mask, BusinessCalendar calendar)
    {
        var rule = new DueRule { Kind = DueRuleKind.SameDay };
        Match m;
        if ((m = DueDayOfMonthRegex().Match(mask.Text)).Success)
        {
            rule = rule with { Kind = DueRuleKind.DayOfMonth, Value = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture) };
            mask.Mask(m);
        }
        else if ((m = DueEndOfMonthRegex().Match(mask.Text)).Success)
        {
            rule = rule with { Kind = DueRuleKind.EndOfMonth };
            mask.Mask(m);
        }
        else if ((m = DueWithinRegex().Match(mask.Text)).Success && TextTools.ParseNumber(m.Groups["n"].Value) is { } n)
        {
            rule = rule with { Kind = m.Groups["biz"].Success ? DueRuleKind.AfterWorkingDays : DueRuleKind.AfterDays, Value = n };
            mask.Mask(m);
        }
        else if ((m = DueWeekdayRegex().Match(mask.Text)).Success && DateExpressionParser.ParseWeekday(m.Groups["day"].Value) is { } wd)
        {
            rule = rule with { Kind = DueRuleKind.WeekdayOfSameWeek, Weekday = wd };
            mask.Mask(m);
        }

        if ((m = DueTimeRegex().Match(mask.Text)).Success && ParseClock(m.Groups["t"].Value, calendar) is { } t)
        {
            rule = rule with { DueTime = t };
            mask.Mask(m);
        }
        return rule;
    }

    // ───────────────────────────── assignees and title ─────────────────────────────

    private static (string? People, string Action, bool FromPattern) SplitCommand(string masked, DirectoryResolver resolver)
    {
        var sentences = TextTools.Sentences(masked).Select(s => TextTools.Squash(s.Text)).Where(s => s.Trim('.', ' ', ',').Length > 2).ToList();
        if (sentences.Count == 0) return (null, "", false);

        foreach (var sentence in sentences)
        {
            // "Assign the vendor checklist to Priya" (action first) vs "Assign Priya to review the checklist" (people first).
            if (AssignActionToPatternRegex().Match(sentence) is { Success: true } assign && Resolves(assign.Groups["people"].Value))
                return (assign.Groups["people"].Value, assign.Groups["action"].Value, true);
            if (AskPatternRegex().Match(sentence) is { Success: true } ask)
                return (ask.Groups["people"].Value, ask.Groups["action"].Value, true);
            if (CreatePatternRegex().Match(sentence) is { Success: true } create)
                return (create.Groups["people"].Success ? create.Groups["people"].Value : null, create.Groups["action"].Value, true);
            if (ColonPatternRegex().Match(sentence) is { Success: true } colon && Resolves(colon.Groups["people"].Value))
                return (colon.Groups["people"].Value, colon.Groups["action"].Value, true);
            if (ModalPatternRegex().Match(sentence) is { Success: true } modal && Resolves(modal.Groups["people"].Value))
                return (modal.Groups["people"].Value, modal.Groups["action"].Value, true);
        }
        return (null, LeadingRequestRegex().Replace(sentences[0], ""), false);

        bool Resolves(string chunk)
        {
            if (resolver.ResolveAssignee(chunk, out _) is not null) return true;
            var parts = SplitPeople(chunk);
            return parts.Count > 0 && parts.All(p => resolver.ResolveAssignee(p, out _) is not null);
        }
    }

    private static void ResolvePeople(string chunk, DirectoryResolver resolver, Guid? currentUserId,
        List<ResolvedEntity> persons, List<ResolvedEntity> teams, List<ResolvedEntity> departments,
        List<string> unresolved, List<DraftNote> notes, ref double confidence)
    {
        var whole = resolver.ResolveAssignee(chunk, out _);
        var parts = whole is not null ? [chunk] : SplitPeople(chunk);
        foreach (var part in parts)
        {
            var e = resolver.ResolveAssignee(part, out var ambiguous);
            var shown = DirectoryResolver.Normalize(part);
            if (e is null)
            {
                var name = TextTools.Squash(part).Trim(' ', ',', '.');
                if (name.Length == 0) continue;
                if (ambiguous.Count > 1)
                {
                    notes.Add(new DraftNote("Assignee", $"\"{name}\" matches several people: {string.Join(", ", ambiguous)} — please choose."));
                    confidence -= 0.15;
                }
                else
                {
                    notes.Add(new DraftNote("Assignee", $"Could not find \"{name}\" in the directory."));
                    confidence -= 0.15;
                }
                unresolved.Add(name);
                continue;
            }
            if (e.Kind == EntityKind.Person && e.Id == currentUserId)
            {
                notes.Add(new DraftNote("Assignee", $"\"{shown}\" is you — not added as an assignee."));
                continue;
            }
            if (e.Score < 0.85) confidence -= 0.05;
            var field = e.Kind switch { EntityKind.Person => persons.Count == 0 ? "PrimaryAssignee" : "Assignee", EntityKind.Team => "Team", _ => "Department" };
            notes.Add(new DraftNote(field, $"\"{shown}\" → {e.Name} ({e.How}, {e.Score.ToString("0.00", CultureInfo.InvariantCulture)})."));
            (e.Kind switch { EntityKind.Person => persons, EntityKind.Team => teams, _ => departments }).Add(e);
        }
    }

    private static List<string> SplitPeople(string chunk) =>
        PeopleSeparatorRegex().Split(chunk).Select(p => p.Trim(' ', ',', '.')).Where(p => p.Length > 0 && !p.Equals("the", StringComparison.OrdinalIgnoreCase)).ToList();

    private static string BuildTitle(string action)
    {
        var t = TextTools.Squash(action);
        t = SpaceBeforePunctuationRegex().Replace(t, "$1");
        t = RepeatedPunctuationRegex().Replace(t, "$1");
        for (var i = 0; i < 4; i++)
        {
            var before = t;
            t = LeadingRequestRegex().Replace(t, "");
            t = TrailingJunkRegex().Replace(t, "");
            t = t.Trim(' ', ',', '.', ';', ':', '-', '!');
            if (t == before) break;
        }
        return TextTools.Truncate(TextTools.Capitalize(t), 120);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static bool IsAdjectiveUse(string text, Match m)
    {
        var word = m.Value.Trim().ToLowerInvariant();
        if (word.Contains(' ', StringComparison.Ordinal) && !word.StartsWith("half", StringComparison.Ordinal) && !word.StartsWith("bi", StringComparison.Ordinal)) return false;
        var next = NextWordRegex().Match(text, m.Index + m.Length);
        return next.Success && next.Index == m.Index + m.Length && !AllowedFollowerRegex().IsMatch(next.Groups["w"].Value);
    }

    private static List<DayOfWeek> ParseDays(string list) =>
        DayWordRegex().Matches(list).Select(x => DateExpressionParser.ParseWeekday(x.Value)).OfType<DayOfWeek>().Distinct().ToList();

    private static TimeOnly? ParseClock(string text, BusinessCalendar calendar)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t is "noon" or "midday" or "12 noon") return DateExpressionParser.Noon;
        if (t.StartsWith("eod", StringComparison.Ordinal) || t.StartsWith("cob", StringComparison.Ordinal) || t.StartsWith("end of", StringComparison.Ordinal)) return calendar.WorkdayEnd;
        var m = ClockRegex().Match(t);
        if (!m.Success) return null;
        var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var min = m.Groups["min"].Success ? int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture) : 0;
        var ap = m.Groups["ap"].Value;
        if (ap.StartsWith('p') && h < 12) h += 12;
        if (ap.StartsWith('a') && h == 12) h = 0;
        return h is >= 0 and <= 23 && min is >= 0 and <= 59 ? new TimeOnly(h, min) : null;
    }

    private static string Clean(string s) => TextTools.Squash(s).Trim(' ', ',', '.', ';');

    /// <summary>A copy of the command in which recognised phrases are blanked out (same length, so offsets stay valid).</summary>
    private sealed class Masker(string text)
    {
        private readonly char[] _chars = text.ToCharArray();
        public string Text => new(_chars);
        public void Mask(Match m) => Mask(m.Index, m.Length);
        public void Mask(int index, int length)
        {
            var from = Math.Max(0, index);
            var to = Math.Min(_chars.Length, index + length);
            for (var i = from; i < to; i++) _chars[i] = ' ';
        }
    }

    // ───────────────────────────── patterns: flags ─────────────────────────────

    [GeneratedRegex(@"(?:,?\s*\b(?:and|also|then)\s+)?\b(?:please\s+)?(?:(?:tell|inform|notify|alert|warn|update|let)\s+me(?:\s+know)?\s+(?:immediately\s+|right\s+away\s+|asap\s+)?(?:if|when|in\s+case|should|whenever|as\s+soon\s+as)\b[^.]*?\b(?:delay(?:ed|s)?|slip(?:s|ping|ped)?|late|behind(?:\s+schedule)?|miss(?:es|ed)?(?:\s+the\s+deadline)?|overdue|at\s+risk)\b|(?:escalate|flag)\s+(?:it\s+|this\s+)?to\s+me\s+if\b[^.]*?\b(?:delay\w*|late|slip\w*|miss\w*|overdue)\b|keep\s+me\s+(?:posted|informed|updated)\s+(?:on|about|of)\s+(?:any\s+)?delays?|alert\s+me\s+(?:on|about|of)\s+(?:any\s+)?delays?)", Opts)]
    private static partial Regex NotifyRegex();

    [GeneratedRegex(@"(?:,?\s*\b(?:and|also)\s+)?(?:\b(?:it\s+|this\s+)?(?:needs?|requires?|will\s+need|should\s+get|must\s+get|is\s+subject\s+to|subject\s+to)\s+my\s+(?:final\s+)?approval\b|\bi\s+(?:want|would\s+like|'d\s+like|’d\s+like|need)\s+to\s+approve(?:\s+(?:it|this|the\s+\w+))?\b|\b(?:with|after|send\s+(?:it\s+)?to\s+me\s+for|for)\s+my\s+approval\b|\bi\s+will\s+approve(?:\s+(?:it|this))?\b)", Opts)]
    private static partial Regex ApprovalRegex();

    [GeneratedRegex(@"\b(?:low[\s-]priority|no\s+rush|not\s+urgent|whenever\s+(?:possible|you\s+can|convenient)|when\s+time\s+permits)\b", Opts)]
    private static partial Regex LowPriorityRegex();

    [GeneratedRegex(@"\b(?:urgent(?:ly)?|asap|a\.s\.a\.p|immediately|right\s+away|at\s+the\s+earliest|top\s+priority|highest\s+priority)\b", Opts)]
    private static partial Regex UrgentRegex();

    [GeneratedRegex(@"\b(?:high[\s-]priority|important|on\s+(?:a\s+)?priority(?:\s+basis)?|priority\s+task)\b", Opts)]
    private static partial Regex HighPriorityRegex();

    [GeneratedRegex(@"\b(?:business|mission)[\s-]critical\b", Opts)]
    private static partial Regex BusinessCriticalRegex();

    [GeneratedRegex(@"(?<!\b(?:business|mission)[\s-])(?<!\bnot\s)\bcritical\b", Opts)]
    private static partial Regex CriticalRegex();

    // Flag words used as predicates ("this is urgent", "mark it critical", "high priority.") — removed from the title;
    // adjectives before a noun ("fix the critical payment bug") are kept.
    [GeneratedRegex(@"(?:,?\s*\b(?:and\s+)?)(?:\b(?:this|it)\s+is\s+|\bit's\s+|\bmark\s+(?:it|this)\s+(?:as\s+)?|\bmake\s+(?:it|this)\s+|\btreat\s+(?:it|this)\s+as\s+|\bas\s+(?:an?\s+)?)?\b(?:very\s+)?(?:urgent|(?:business|mission)[\s-]critical|critical|important|(?:high|top|highest|low)[\s-]priority(?:\s+(?:task|item))?)\b(?=\s*(?:[.,;:!]|$|\band\b))|\b(?:urgently|asap|a\.s\.a\.p\.?|immediately|right\s+away|at\s+the\s+earliest|on\s+(?:a\s+)?(?:top\s+)?priority(?:\s+basis)?|with\s+(?:high|top)\s+priority)\b", Opts)]
    private static partial Regex FlagPhraseRegex();

    [GeneratedRegex(@"^\s*(?:urgent|important|critical|business[\s-]critical|high\s+priority|fyi)\s*[:\-–—]\s*", Opts)]
    private static partial Regex FlagPrefixRegex();

    // ───────────────────────────── patterns: follow-up ─────────────────────────────

    [GeneratedRegex(@"(?:,?\s*\b(?:and|also|then)\s+)?\b(?:please\s+)?(?:follow[\s-]?up|followup|check\s+in|check\s+on\s+(?:it|this|them|progress)|chase(?:\s+(?:them|him|her|it))?|remind\s+(?:them|him|her|the\s+team|everyone)|ping\s+(?:them|him|her))\s+(?:with\s+(?:them|him|her|the\s+team)\s+)?(?:on\s+(?:this|it)\s+)?(?<freq>daily|every\s*day|each\s+day|every\s+working\s+day|every\s+other\s+day|on\s+alternate\s+days|alternate\s+days|every\s+(?<n>" + Num + @")\s+(?:working\s+|business\s+)?days|weekly|every\s+week|once\s+a\s+week|twice\s+a\s+week|fortnightly|every\s+(?:two|2)\s+weeks|every\s+other\s+week|monthly|every\s+month|(?:every|on)\s+(?<days>" + DayList + @"))(?:\s+at\s+(?<time>\d{1,2}(?:[:.]\d{2})?\s*(?:am|pm)?|noon))?\b", Opts)]
    private static partial Regex FollowUpRegex();

    [GeneratedRegex(@"(?:,?\s*\b(?:and|with)\s+)?\b(?<freq>daily|weekly|fortnightly|monthly)\s+follow[\s-]?ups?\b", Opts)]
    private static partial Regex FollowUpAdjectiveRegex();

    [GeneratedRegex(@"(?:,?\s*\b(?:and|but)\s+)?\b(?:no|without|don'?t|do\s+not|skip|no\s+need\s+to|no\s+need\s+for)\s+(?:any\s+)?(?:automatic\s+)?(?:follow[\s-]?ups?|reminders?|chasing)\b(?:\s+(?:is\s+)?(?:needed|required|necessary))?", Opts)]
    private static partial Regex NoFollowUpRegex();

    // ───────────────────────────── patterns: recurrence ─────────────────────────────

    [GeneratedRegex(@"\b(?:recurring|repeating|repeat(?:s|ed)?|routine|periodic(?:al)?|regular)\b", Opts)]
    private static partial Regex RecurringWordRegex();

    [GeneratedRegex(@"\b(?:on\s+)?(?:the\s+)?(?<d>" + Dom + @")(?:st|nd|rd|th)?\s+(?:day\s+)?of\s+(?:every|each)\s+month\b|\b(?:every|each)\s+month\s+on\s+the\s+(?<d>" + Dom + @")(?:st|nd|rd|th)?\b|\bmonthly\s+on\s+the\s+(?<d>" + Dom + @")(?:st|nd|rd|th)?\b", Opts)]
    private static partial Regex NthOfMonthRegex();

    [GeneratedRegex(@"\b(?:on\s+)?(?:the\s+)?(?<which>last|first)\s+(?<working>working\s+|business\s+)?day\s+of\s+(?:the|every|each)\s+month\b|\b(?:every|each)\s+month\s+on\s+the\s+(?<which>last|first)\s+(?<working>working\s+|business\s+)?day\b|\b(?:every|each)\s+(?<monthend>month[\s-]end)\b", Opts)]
    private static partial Regex SpecialDayOfMonthRegex();

    [GeneratedRegex(@"\b(?:every|each|once\s+(?:a|per|every)|per)\s+(?:financial|fiscal)\s+year\b|\b(?:annually|yearly)\s+(?:per|each|every)\s+(?:financial|fiscal)\s+year\b", Opts)]
    private static partial Regex FinancialYearRegex();

    [GeneratedRegex(@"\b(?:every|each)\s+(?:working|business|week)\s?day\b|\bon\s+(?:all\s+)?(?:working|business)\s+days\b|\bon\s+weekdays\b|\bdaily\s+(?:except|excluding)\s+(?:weekends|holidays)\b", Opts)]
    private static partial Regex WorkingDaysRegex();

    [GeneratedRegex(@"\b(?:every|each)\s+(?<days>" + DayList + @")\b|\bon\s+(?<days>(?:" + Day + @")s(?:\s*(?:,|and|&)\s*(?:" + Day + @")s)*)\b", Opts)]
    private static partial Regex EveryWeekdaysRegex();

    [GeneratedRegex(@"\b(?:fortnightly|bi-?weekly|every\s+(?:two|2|other|alternate)\s+weeks?|every\s+fortnight)\b(?:\s+on\s+(?<day>(?:" + Day + @"))s?)?", Opts)]
    private static partial Regex FortnightlyRegex();

    [GeneratedRegex(@"\bevery\s+(?<n>" + Num + @")\s+(?<u>days|weeks|months|years)\b", Opts)]
    private static partial Regex EveryNRegex();

    [GeneratedRegex(@"\b(?:weekly|every\s+week|each\s+week|once\s+a\s+week)\b(?:\s+on\s+(?<day>(?:" + Day + @"))s?)?", Opts)]
    private static partial Regex WeeklyRegex();

    [GeneratedRegex(@"\b(?:quarterly|every\s+quarter|each\s+quarter|once\s+a\s+quarter)\b", Opts)]
    private static partial Regex QuarterlyRegex();

    [GeneratedRegex(@"\b(?:half[\s-]?yearly|semi[\s-]?annual(?:ly)?|bi[\s-]?annual(?:ly)?|twice\s+a\s+year|every\s+half[\s-]year)\b", Opts)]
    private static partial Regex HalfYearlyRegex();

    [GeneratedRegex(@"\b(?:annually|annual|yearly|every\s+year|each\s+year|once\s+a\s+year)\b", Opts)]
    private static partial Regex YearlyRegex();

    [GeneratedRegex(@"\b(?:monthly|every\s+month|each\s+month|once\s+a\s+month)\b(?:\s+on\s+the\s+(?<d>" + Dom + @")(?:st|nd|rd|th)?\b)?", Opts)]
    private static partial Regex MonthlyRegex();

    [GeneratedRegex(@"\b(?:daily|every\s*day|each\s+day|once\s+a\s+day)\b", Opts)]
    private static partial Regex DailyRegex();

    [GeneratedRegex(@"^\s*(?:(?:in\s+the\s+)?mornings?\b|at\s+(?<t>\d{1,2}(?:[:.]\d{2})?\s*(?:am|pm)|\d{1,2}:\d{2})\b)", Opts)]
    private static partial Regex CreateTimeRegex();

    [GeneratedRegex(@"\b(?:by|due|before|till|until)\s+(?:the\s+)?$", Opts)]
    private static partial Regex DuePrefixRegex();

    [GeneratedRegex(@"(?:,?\s*\b(?:and\s+)?(?:it\s+)?(?:is\s+|should\s+be\s+)?)?\b(?:due\s+(?:by\s+|on\s+|before\s+)?|by\s+|before\s+)(?:the\s+)?(?<d>" + Dom + @")(?:st|nd|rd|th)(?:\s+of\s+(?:the|every|each)\s+month)?\b", Opts)]
    private static partial Regex DueDayOfMonthRegex();

    [GeneratedRegex(@"\b(?:due\s+)?(?:by|before|on)\s+(?:the\s+)?(?:month[\s-]end|end\s+of\s+(?:the\s+)?month|last\s+day\s+of\s+the\s+month)\b", Opts)]
    private static partial Regex DueEndOfMonthRegex();

    [GeneratedRegex(@"\b(?:due\s+)?within\s+(?<n>" + Num + @")\s+(?<biz>working\s+|business\s+)?days\b", Opts)]
    private static partial Regex DueWithinRegex();

    [GeneratedRegex(@"\b(?:due\s+(?:by\s+|on\s+)?|by\s+)(?<day>(?:" + Day + @"))\b", Opts)]
    private static partial Regex DueWeekdayRegex();

    [GeneratedRegex(@"\b(?:due\s+)?(?:by|before|at)\s+(?<t>noon|midday|\d{1,2}(?:[:.]\d{2})?\s*(?:am|pm)|\d{1,2}:\d{2}|eod|cob|end\s+of\s+(?:the\s+)?day)\b", Opts)]
    private static partial Regex DueTimeRegex();

    // ───────────────────────────── patterns: dates, people, title ─────────────────────────────

    [GeneratedRegex(@"\b(?:starting|start(?:ing)?\s+(?:on|from)|from|beginning|commencing|w\.?e\.?f\.?)\s*(?:on\s+|from\s+)?(?:the\s+)?$", Opts)]
    private static partial Regex StartPrepositionRegex();

    [GeneratedRegex(@"\b(?:by|before|due(?:\s+(?:on|by))?|till|until|no\s+later\s+than|latest\s+by|deadline(?:\s+is)?|on|for)\s+(?:the\s+)?$", Opts)]
    private static partial Regex DuePrepositionRegex();

    [GeneratedRegex(@"^\s*(?:please\s+|kindly\s+|can\s+you\s+|could\s+you\s+)?(?:ask|tell|get|have|request|remind|instruct|direct|assign|make)\s+(?<people>.+?)\s+to\s+(?<action>.+)$", Opts)]
    private static partial Regex AskPatternRegex();

    [GeneratedRegex(@"^\s*(?:please\s+|kindly\s+)?(?:create|add|set\s*up|make|open|raise|log|schedule|start|put\s+in)\s+(?:a|an|one)?\s*(?:new\s+)?(?:(?:recurring|repeating|routine|periodic|regular)\s+)?(?:task|todo|to-do|action\s+item|activity|reminder|job|assignment)\s+(?:for\s+(?<people>.+?)\s+)?(?:to\s+|:\s*|-\s*)(?<action>.+)$", Opts)]
    private static partial Regex CreatePatternRegex();

    [GeneratedRegex(@"^\s*(?:please\s+)?assign\s+(?<action>.+?)\s+to\s+(?<people>[^,.;]+?)[\s,.;!]*$", Opts)]
    private static partial Regex AssignActionToPatternRegex();

    [GeneratedRegex(@"^\s*(?<people>[^,:]{2,60}?)\s*[,:]\s*(?:please\s+|kindly\s+)?(?<action>.+)$", Opts)]
    private static partial Regex ColonPatternRegex();

    [GeneratedRegex(@"^\s*(?<people>.{2,60}?)\s+(?:should|must|needs?\s+to|has\s+to|have\s+to|is\s+to|are\s+to|will)\s+(?<action>.+)$", Opts)]
    private static partial Regex ModalPatternRegex();

    [GeneratedRegex(@"\s*(?:,|\band\b|&|\bplus\b|\balong\s+with\b|\bwith\b)\s*", Opts)]
    private static partial Regex PeopleSeparatorRegex();

    [GeneratedRegex(@"^(?:please|kindly|can\s+you|could\s+you|i\s+want\s+(?:you\s+)?to|i\s+need\s+(?:you\s+)?to|we\s+need\s+to|need\s+to|to)\s+", Opts)]
    private static partial Regex LeadingRequestRegex();

    [GeneratedRegex(@"(?:\s*\b(?:and|then|also|so|but|or|by|on|before|till|until|for|from|due|at|with|the)\b\s*|\s*[,;:\-–—]\s*)$", Opts)]
    private static partial Regex TrailingJunkRegex();

    [GeneratedRegex(@"\b(?:for|under|in|on|as\s+part\s+of)\s+(?:the\s+)?$", Opts)]
    private static partial Regex ProjectLeadRegex();

    [GeneratedRegex(@"\s+([,.;:!])", Opts)]
    private static partial Regex SpaceBeforePunctuationRegex();

    [GeneratedRegex(@"([,;:])(?:\s*[,;:])+", Opts)]
    private static partial Regex RepeatedPunctuationRegex();

    [GeneratedRegex(@"^\s+(?:project|programme|program|initiative)\b", Opts)]
    private static partial Regex ProjectTailRegex();

    [GeneratedRegex(@"\G\s+(?<w>[a-z]+)", Opts)]
    private static partial Regex NextWordRegex();

    // Words that may follow a frequency adjective without making it describe a noun; "a monthly reminder / task"
    // describes the task itself, so it counts as recurrence too.
    [GeneratedRegex(@"^(?:by|on|at|and|until|till|from|starting|before|for|basis|in|with|to|or|every|each|due|then|also|so|but|task|tasks|reminder|reminders|activity|activities|job|jobs|todo|routine|check|checks|follow)$", Opts)]
    private static partial Regex AllowedFollowerRegex();

    [GeneratedRegex(@"\b(?:" + Day + @")\b", Opts)]
    private static partial Regex DayWordRegex();

    [GeneratedRegex(@"^(?<h>\d{1,2})(?:[:.](?<min>\d{2}))?\s*(?<ap>am|pm|a\.m\.?|p\.m\.?)?$", Opts)]
    private static partial Regex ClockRegex();
}
