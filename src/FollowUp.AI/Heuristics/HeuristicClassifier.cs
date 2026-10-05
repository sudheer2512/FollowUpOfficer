using System.Globalization;
using System.Text.RegularExpressions;
using FollowUp.AI.Dates;
using FollowUp.AI.Text;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Domain.Rules;

namespace FollowUp.AI.Heuristics;

/// <summary>
/// Deterministic message classifier: category, facts (suggested status, blocker, commitment date, proposed due date,
/// progress %, urgency) and the signals that explain the decision.
/// </summary>
/// <remarks>
/// <para>Each rule that fires scores its category (strong explicit phrases 0.85–0.9, weaker cues 0.5–0.75). The highest
/// score wins; ties are broken by precedence: Escalation › Rejection › Completion › Blocker › DeadlineChange › Delay ›
/// Approval › RequestForHelp › Clarification › Commitment › ProgressUpdate › Information › Unrelated.</para>
/// <para>A message that reports a blocker <em>and</em> promises a date ("We are waiting for API credentials from the
/// provider. We should finish by Friday.") is classified as <see cref="MessageCategory.Blocker"/> — the blocker is what
/// needs follow-up action — while the promise is kept in <see cref="MessageFacts.CommitmentDate"/> so Core still records
/// the commitment.</para>
/// Stateless and thread-safe.
/// </remarks>
public sealed partial class HeuristicClassifier
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly MessageCategory[] Precedence =
    [
        MessageCategory.Escalation, MessageCategory.Rejection, MessageCategory.Completion, MessageCategory.Blocker,
        MessageCategory.DeadlineChange, MessageCategory.Delay, MessageCategory.Approval, MessageCategory.RequestForHelp,
        MessageCategory.Clarification, MessageCategory.Commitment, MessageCategory.ProgressUpdate, MessageCategory.Information,
        MessageCategory.Unrelated,
    ];

    private readonly HeuristicCommitmentExtractor _commitments;

    /// <summary>Creates a classifier (optionally sharing a commitment extractor).</summary>
    public HeuristicClassifier(HeuristicCommitmentExtractor? commitments = null) => _commitments = commitments ?? new HeuristicCommitmentExtractor();

    /// <summary>Classifies a message, optionally in the context of the task it was matched to.</summary>
    public MessageClassification Classify(MessageForAnalysis message, TaskCandidate? task, BusinessCalendar calendar)
    {
        var subject = SubjectPrefixRegex().Replace(message.Subject ?? "", "").Trim();
        var body = TextTools.StripQuotedReply(message.Body);
        var text = subject.Length > 0 && !body.Contains(subject, StringComparison.OrdinalIgnoreCase) ? subject + ".\n" + body : body;
        var s = new State(text);

        DetectCompletion(s);
        DetectBlocker(s);
        DetectDelayAndDeadlineChange(s, message.SentAtUtc, calendar);
        DetectPhrases(s, ApprovalRegex(), MessageCategory.Approval, 0.9, "approval", ApprovalBlockerRegex());
        DetectPhrases(s, RejectionRegex(), MessageCategory.Rejection, 0.88, "rejection / rework requested");
        DetectPhrases(s, EscalationRegex(), MessageCategory.Escalation, 0.9, "escalation");
        DetectPhrases(s, HelpRegex(), MessageCategory.RequestForHelp, 0.85, "request for help");
        DetectClarification(s);
        DetectProgress(s);
        DetectPhrases(s, InformationRegex(), MessageCategory.Information, 0.65, "information");

        // "We completed part 1, part 2 is pending from the customer": something is still outstanding, so the
        // completion is partial and the blocker is what needs follow-up.
        if (s.Score(MessageCategory.Completion) >= 0.85 && s.Score(MessageCategory.Blocker) >= 0.75)
        {
            s.Scores[MessageCategory.Completion] = 0.6;
            s.Partial = true;
            s.Signals.Add("completion is partial: something is still outstanding");
        }

        // Commitments by the writer.
        var commitments = _commitments.Extract(text, message.SentAtUtc, calendar);
        var commitment = commitments.OrderByDescending(c => c.Confidence).FirstOrDefault();
        if (commitment is not null)
        {
            s.Hit(MessageCategory.Commitment, Math.Min(0.85, commitment.Confidence),
                $"commitment: \"{TextTools.Truncate(commitment.Text, 80)}\" → {TextTools.Show(commitment.Date)} ({commitment.Confidence.ToString("0.00", CultureInfo.InvariantCulture)})");
            s.FactSignals.Add($"commitment date {TextTools.Show(commitment.Date)}");
        }

        // Low-signal / social messages.
        var workScore = s.Scores
            .Where(kv => kv.Key is not (MessageCategory.Information or MessageCategory.Unrelated) && !(kv.Key == MessageCategory.Clarification && kv.Value <= 0.55))
            .Select(kv => kv.Value).DefaultIfEmpty(0).Max();
        if (workScore < 0.5 && SocialRegex().Match(text) is { Success: true } social)
            s.Hit(MessageCategory.Unrelated, 0.75, $"social / non-work content: \"{social.Value}\"");

        var urgent = UrgentRegex().Match(text);
        if (urgent.Success && !NegatedBefore(text, urgent.Index)) s.Signals.Add($"urgent language: \"{urgent.Value}\"");

        // Task context: does the message talk about the task at all?
        if (task is not null)
        {
            var titleTokens = TextTools.ContentTokens(task.Title).Where(t => !TextTools.WeakWords.Contains(t)).ToHashSet();
            var shared = TextTools.ContentTokens(text).Where(titleTokens.Contains).Distinct().ToList();
            if (shared.Count > 0) s.Signals.Add($"mentions task words: {string.Join(", ", shared.Take(5))}");
        }

        MessageCategory category;
        double confidence;
        if (s.Scores.Count == 0)
        {
            var words = TextTools.Words(text).Count();
            (category, confidence) = words >= 12 ? (MessageCategory.Information, 0.45) : (MessageCategory.Unrelated, 0.5);
            s.Signals.Add("no status, commitment or request language found");
        }
        else
        {
            var top = s.Scores.OrderByDescending(kv => kv.Value).ThenBy(kv => Array.IndexOf(Precedence, kv.Key)).First();
            (category, confidence) = (top.Key, top.Value);
        }

        var status = category switch
        {
            MessageCategory.Completion => WorkStatus.CompletedByAssignee,
            MessageCategory.Blocker => s.BlockerStatus ?? WorkStatus.Blocked,
            _ when s.BlockerStatus is not null && s.Score(MessageCategory.Blocker) >= 0.75 => s.BlockerStatus,
            _ when s.Partial => WorkStatus.PartiallyCompleted,
            _ when s.InProgress || s.Percent is > 0 and < 100 => WorkStatus.InProgress,
            _ => (WorkStatus?)null,
        };
        if (status is { } st) s.FactSignals.Add($"suggested status {st}");

        var facts = new MessageFacts
        {
            SuggestedStatus = status,
            Blocker = category == MessageCategory.Blocker || s.Score(MessageCategory.Blocker) >= 0.75 ? s.BlockerText : null,
            CommitmentDate = commitment?.Date,
            CommitmentText = commitment?.Text,
            ProposedDueDate = s.ProposedDueDate,
            ProgressPercent = s.Percent,
            Urgent = urgent.Success && !NegatedBefore(text, urgent.Index),
            Signals = s.FactSignals.Distinct().ToList(),
        };

        return new MessageClassification(category, Math.Round(confidence, 2), Summarize(category, facts), facts, s.Signals.Distinct().ToList());
    }

    // ───────────────────────────── detectors ─────────────────────────────

    private static void DetectCompletion(State s)
    {
        foreach (var (regex, score) in new (Regex, double)[] { (CompletionRegex(), 0.9), (CompletionFirstPersonRegex(), 0.9), (CompletionShortRegex(), 0.85) })
        {
            foreach (Match m in regex.Matches(s.Text))
            {
                if (NotYetBefore(s.Text, m.Index) || IsInQuestion(s.Text, m.Index)) continue;
                s.Hit(MessageCategory.Completion, score, $"completion: \"{TextTools.Squash(m.Value)}\"");
                break;
            }
        }
    }

    private static void DetectBlocker(State s)
    {
        foreach (Match m in WaitingRegex().Matches(s.Text))
        {
            if (NegatedBefore(s.Text, m.Index) || IsInQuestion(s.Text, m.Index)) continue;
            var what = CleanBlocker(m.Groups["what"].Value);
            if (what.Length < 3) continue;
            var status = ExternalPartyRegex().IsMatch(what) ? WorkStatus.WaitingForExternalParty
                : ApprovalWordRegex().IsMatch(what) ? WorkStatus.WaitingForApproval
                : InformationWordRegex().IsMatch(what) ? WorkStatus.WaitingForInformation
                : WorkStatus.Blocked;
            s.SetBlocker(what, status);
            s.Hit(MessageCategory.Blocker, 0.88, $"waiting for \"{what}\" → {status}");
            return;
        }

        // "The vendor has not shared the API keys yet" — something someone has not provided.
        var notProvided = NotProvidedRegex().Match(s.Text);
        var notProvidedText = notProvided.Success ? CleanBlocker(notProvided.Value) : "";

        foreach (Match m in BlockedRegex().Matches(s.Text))
        {
            if (NegatedBefore(s.Text, m.Index) || IsInQuestion(s.Text, m.Index)) continue;
            var what = CleanBlocker(m.Groups["what"].Value);
            if (what.Length < 3 && notProvidedText.Length >= 3) what = notProvidedText;
            var status = what.Length >= 3 && ExternalPartyRegex().IsMatch(what) ? WorkStatus.WaitingForExternalParty : WorkStatus.Blocked;
            s.SetBlocker(what.Length >= 3 ? what : TextTools.Squash(m.Value), status);
            s.Hit(MessageCategory.Blocker, 0.88, $"blocked: \"{TextTools.Squash(m.Value)}\" → {status}");
            return;
        }

        if (notProvidedText.Length >= 3)
        {
            var status = ExternalPartyRegex().IsMatch(notProvidedText) ? WorkStatus.WaitingForExternalParty : WorkStatus.WaitingForInformation;
            s.SetBlocker(notProvidedText, status);
            s.Hit(MessageCategory.Blocker, 0.8, $"not provided yet: \"{notProvidedText}\" → {status}");
            return;
        }

        foreach (Match m in DependencyRegex().Matches(s.Text))
        {
            if (NegatedBefore(s.Text, m.Index)) continue;
            var what = CleanBlocker(m.Groups["what"].Value);
            if (what.Length < 3) continue;
            var status = ExternalPartyRegex().IsMatch(what) ? WorkStatus.WaitingForExternalParty : WorkStatus.Blocked;
            s.SetBlocker(what, status);
            s.Hit(MessageCategory.Blocker, 0.75, $"dependency: \"{what}\" → {status}");
            return;
        }
    }

    private static void DetectDelayAndDeadlineChange(State s, DateTime sentAtUtc, BusinessCalendar calendar)
    {
        var today = calendar.Today(sentAtUtc);
        foreach (var d in DateExpressionParser.Parse(s.Text, sentAtUtc, calendar))
        {
            if (d.Date < today) continue;
            var before = s.Text[Math.Max(0, d.Index - 70)..d.Index];
            var m = DeadlineChangeTriggerRegex().Match(before);
            if (!m.Success) continue;
            s.ProposedDueDate ??= d.Date;
            s.Hit(MessageCategory.DeadlineChange, 0.9, $"deadline change: \"{TextTools.Squash(m.Value + d.Text)}\" → {TextTools.Show(d.Date)}");
            s.FactSignals.Add($"proposed due date {TextTools.Show(d.Date)}");
            break;
        }
        if (s.ProposedDueDate is null && DeadlineChangeNoDateRegex().Match(s.Text) is { Success: true } nd)
            s.Hit(MessageCategory.DeadlineChange, 0.75, $"deadline change requested: \"{TextTools.Squash(nd.Value)}\"");

        foreach (Match m in DelayRegex().Matches(s.Text))
        {
            if (NegatedBefore(s.Text, m.Index) || DelayGuardRegex().IsMatch(s.Text[Math.Max(0, m.Index - 40)..m.Index])) continue;
            s.Hit(MessageCategory.Delay, 0.85, $"delay: \"{TextTools.Squash(m.Value)}\"");
            break;
        }
    }

    private static void DetectClarification(State s)
    {
        var m = ClarificationRegex().Match(s.Text);
        if (m.Success)
        {
            s.Hit(MessageCategory.Clarification, 0.85, $"clarification: \"{TextTools.Squash(m.Value)}\"");
            return;
        }
        if (s.Text.Contains('?', StringComparison.Ordinal) && !SocialRegex().IsMatch(s.Text))
            s.Hit(MessageCategory.Clarification, 0.55, "contains a question");
    }

    private static void DetectProgress(State s)
    {
        var pct = PercentRegex().Match(s.Text);
        if (pct.Success && int.TryParse(pct.Groups["pct"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 100)
        {
            s.Percent = p;
            s.FactSignals.Add($"progress {p}%");
            if (p == 100) s.Hit(MessageCategory.Completion, 0.9, "progress: 100%");
            else s.Hit(MessageCategory.ProgressUpdate, 0.82, $"progress: {p}%");
        }
        var partial = PartialRegex().Match(s.Text);
        if (partial.Success)
        {
            s.Partial = true;
            s.Hit(MessageCategory.ProgressUpdate, 0.8, $"partially done: \"{TextTools.Squash(partial.Value)}\"");
        }
        foreach (Match m in ProgressRegex().Matches(s.Text))
        {
            if (NegatedBefore(s.Text, m.Index) || IsInQuestion(s.Text, m.Index)) continue;
            s.InProgress = true;
            s.Hit(MessageCategory.ProgressUpdate, 0.75, $"progress: \"{TextTools.Squash(m.Value)}\"");
            break;
        }
        if (NotDoneRegex().Match(s.Text) is { Success: true } notDone)
            s.Hit(MessageCategory.ProgressUpdate, 0.6, $"not finished yet: \"{TextTools.Squash(notDone.Value)}\"");
        if (WeakProgressRegex().Match(s.Text) is { Success: true } weak && !IsInQuestion(s.Text, weak.Index))
            s.Hit(MessageCategory.ProgressUpdate, 0.6, $"status note: \"{TextTools.Squash(weak.Value)}\"");
    }

    private static void DetectPhrases(State s, Regex regex, MessageCategory category, double score, string label, Regex? blockedBy = null)
    {
        foreach (Match m in regex.Matches(s.Text))
        {
            if (NegatedBefore(s.Text, m.Index)) continue;
            if (blockedBy is not null && blockedBy.IsMatch(s.Text[Math.Max(0, m.Index - 30)..m.Index])) continue;
            s.Hit(category, score, $"{label}: \"{TextTools.Squash(m.Value)}\"");
            return;
        }
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static string CleanBlocker(string raw)
    {
        var w = TextTools.Squash(raw);
        var cut = BlockerCutRegex().Match(w);
        if (cut.Success) w = w[..cut.Index];
        w = LeadingArticleRegex().Replace(w, "").Trim(' ', ',', '.', ':', '-');
        return TextTools.Truncate(w, 120);
    }

    /// <summary>A negation ("no", "not", "without", "n't") within the three words before the match, in the same sentence.</summary>
    private static bool NegatedBefore(string text, int index)
    {
        var start = Math.Max(0, index - 30);
        var window = text[start..index];
        var sentenceBreak = window.LastIndexOfAny(['.', '!', '?', '\n']);
        if (sentenceBreak >= 0) window = window[(sentenceBreak + 1)..];
        return NegationBeforeRegex().IsMatch(window);
    }

    /// <summary>Negation or future / conditional wording before a completion phrase ("not yet done", "once done", "will be done").</summary>
    private static bool NotYetBefore(string text, int index)
    {
        var start = Math.Max(0, index - 40);
        var window = text[start..index];
        var sentenceBreak = window.LastIndexOfAny(['.', '!', '?', '\n']);
        if (sentenceBreak >= 0) window = window[(sentenceBreak + 1)..];
        return NotYetRegex().IsMatch(window);
    }

    private static bool IsInQuestion(string text, int index)
    {
        var end = text.IndexOfAny(['.', '!', '?', '\n'], index);
        return end >= 0 && text[end] == '?';
    }

    private static string Summarize(MessageCategory category, MessageFacts f)
    {
        var main = category switch
        {
            MessageCategory.Completion => "Reports the work as completed.",
            MessageCategory.Blocker => f.Blocker is { Length: > 0 } b ? $"Blocked — {Describe(f.SuggestedStatus)}: {b}." : "Reports a blocker.",
            MessageCategory.Commitment => f.CommitmentDate is { } cd ? $"Commits to a date: {TextTools.Show(cd)}." : "Makes a commitment.",
            MessageCategory.Delay => "Reports a delay.",
            MessageCategory.DeadlineChange => f.ProposedDueDate is { } pd ? $"Asks to move the deadline to {TextTools.Show(pd)}." : "Asks to change the deadline.",
            MessageCategory.Clarification => "Asks for clarification.",
            MessageCategory.Approval => "Approves / gives the go-ahead.",
            MessageCategory.Rejection => "Rejects the work or asks for rework.",
            MessageCategory.Escalation => "Escalation — needs management attention.",
            MessageCategory.RequestForHelp => "Asks for help.",
            MessageCategory.ProgressUpdate => f.ProgressPercent is { } p ? $"Progress update ({p}% done)." : "Progress update.",
            MessageCategory.Information => "Shares information.",
            MessageCategory.Unrelated => "Not related to task work.",
            _ => "Unclassified.",
        };
        if (category != MessageCategory.Commitment && f.CommitmentDate is { } c) main += $" Commits to {TextTools.Show(c)}.";
        if (f.Urgent) main += " Marked urgent.";
        return main;

        static string Describe(WorkStatus? s) => s switch
        {
            WorkStatus.WaitingForExternalParty => "waiting for an external party",
            WorkStatus.WaitingForInformation => "waiting for information",
            WorkStatus.WaitingForApproval => "waiting for approval",
            _ => "blocked",
        };
    }

    private sealed class State(string text)
    {
        public string Text { get; } = text;
        public Dictionary<MessageCategory, double> Scores { get; } = [];
        public List<string> Signals { get; } = [];
        public List<string> FactSignals { get; } = [];
        public WorkStatus? BlockerStatus { get; private set; }
        public string? BlockerText { get; private set; }
        public DateOnly? ProposedDueDate { get; set; }
        public int? Percent { get; set; }
        public bool Partial { get; set; }
        public bool InProgress { get; set; }

        public double Score(MessageCategory c) => Scores.GetValueOrDefault(c);

        public void Hit(MessageCategory c, double score, string signal)
        {
            if (score > Scores.GetValueOrDefault(c)) Scores[c] = score;
            Signals.Add(signal);
        }

        public void SetBlocker(string text, WorkStatus status)
        {
            BlockerText = text;
            BlockerStatus = status;
            FactSignals.Add($"blocker: {text}");
        }
    }

    // ───────────────────────────── patterns ─────────────────────────────

    [GeneratedRegex(@"^\s*(?:(?:re|fw|fwd|aw|sv)\s*:\s*)+", Opts)]
    private static partial Regex SubjectPrefixRegex();

    [GeneratedRegex(@"\b(?:has|have|is|are|was|were|got|been|'s|’s|'ve|’ve)\s+(?:now\s+|all\s+|fully\s+|successfully\s+|finally\s+|already\s+)?(?:been\s+)?(?:completed|done|finished|deployed|delivered|closed|resolved|fixed|submitted|shipped|implemented|uploaded|filed|signed\s+off|handed\s+over|live|wrapped\s+up)\b", Opts)]
    private static partial Regex CompletionRegex();

    [GeneratedRegex(@"\b(?:i|we)\s+(?:(?:have|'ve|’ve|just|finally|successfully|now)\s+)*(?:completed|finished|deployed|delivered|closed|resolved|fixed|submitted|shipped|implemented|uploaded|filed|wrapped\s+up)\b", Opts)]
    private static partial Regex CompletionFirstPersonRegex();

    [GeneratedRegex(@"(?:^|[.!\n]\s*)(?:task\s+|work\s+|it'?s\s+)?(?:all\s+)?(?:done|completed|finished|resolved|fixed|closed|deployed|delivered)\s*(?:[.!]|$)|\b(?:went\s+live|is\s+(?:now\s+)?live|now\s+live|all\s+done)\b", Opts | RegexOptions.Multiline)]
    private static partial Regex CompletionShortRegex();

    [GeneratedRegex(@"(?:n't|n’t|'ll|’ll|\b(?:not|never|yet\s+to|will|should|shall|going\s+to|once|when|after|if|until|before|almost|nearly|partially|partly|be\s+able\s+to|expect(?:ed)?\s+to|hope\s+to|to\s+be))\b[^.!?\n]*$", Opts)]
    private static partial Regex NotYetRegex();

    [GeneratedRegex(@"\b(?:waiting|awaiting|waiting\s+on)\s+(?:for\s+|on\s+)?(?<what>[^.;!?\n]{3,160})|\bpending\s+(?:from|with|at|on)\s+(?<what>[^.;!?\n]{3,160})", Opts)]
    private static partial Regex WaitingRegex();

    [GeneratedRegex(@"\b(?:blocked|stuck|held\s+up|on\s+hold|can(?:no|')t\s+proceed|can’t\s+proceed|unable\s+to\s+proceed|not\s+able\s+to\s+proceed|showstopper|show-stopper|impediment|roadblock)\b(?:\s+(?:by|on|with|at|due\s+to|because\s+of|as)\s+(?<what>[^.;!?\n]{3,160}))?", Opts)]
    private static partial Regex BlockedRegex();

    [GeneratedRegex(@"\b(?:dependent\s+on|depends\s+on|dependency\s+on)\s+(?<what>[^.;!?\n]{3,160})", Opts)]
    private static partial Regex DependencyRegex();

    [GeneratedRegex(@"\s+(?:and|so|but|which|hence|therefore|because|since|after\s+which|before\s+we|once\s+(?:we|it)|then)\b|,|\s+to\s+(?:proceed|continue|start|complete|finish|move)\b|\s+(?:we|i)\s+(?:will|should|can|could|'ll)\b", Opts)]
    private static partial Regex BlockerCutRegex();

    [GeneratedRegex(@"^(?:for|on|the)\s+", Opts)]
    private static partial Regex LeadingArticleRegex();

    [GeneratedRegex(@"\b(?:vendors?|providers?|suppliers?|customers?|clients?|government|govt|ministry|authorit(?:y|ies)|regulators?|banks?|third[\s-]part(?:y|ies)|partners?|consultants?|auditors?|agenc(?:y|ies)|fta|tax\s+department|municipality|court|insurers?|courier|landlord|contractors?|distributors?|dealers?|oem|manufacturers?|external|their\s+side|other\s+side)\b", Opts)]
    private static partial Regex ExternalPartyRegex();

    [GeneratedRegex(@"\b(?:approvals?|sign[\s-]?off|go[\s-]?ahead|green\s+light|nod|authori[sz]ation|consent|sanction)\b", Opts)]
    private static partial Regex ApprovalWordRegex();

    [GeneratedRegex(@"\b(?:info|information|details|clarifications?|clarity|inputs?|data|requirements?|confirmation|response|reply|feedback|answers?|decision|documents?|specs?|specifications?|access|credentials|logins?|passwords?|keys?|files?|samples?)\b", Opts)]
    private static partial Regex InformationWordRegex();

    [GeneratedRegex(@"\b(?:move|shift|extend|push|change|reschedule|postpone|revise|defer)\s+(?:the\s+|our\s+|this\s+)?(?:deadline|due\s+date|target\s+date|end\s+date|date|delivery|timeline|completion|it|this)?\s*(?:to|till|until|by|for)\s+(?:the\s+)?$|\b(?:extension|more\s+time|time|extend(?:ed)?)\s+(?:till|until|to|up\s*to|upto|by)\s+(?:the\s+)?$|\b(?:revised|new|updated)\s+(?:deadline|due\s+date|target\s+date|eta|timeline|date)\s*(?:is|:|will\s+be|of|-)?\s*(?:the\s+)?$|\bneed\s+(?:it\s+|time\s+|this\s+)?(?:till|until|up\s*to)\s+(?:the\s+)?$", Opts)]
    private static partial Regex DeadlineChangeTriggerRegex();

    [GeneratedRegex(@"\b(?:move|extend|push|change|reschedule|postpone|revise)\s+the\s+(?:deadline|due\s+date|target\s+date)\b|\b(?:need|request(?:ing)?|want)\s+(?:an?\s+)?extension\b|\bextension\s+(?:of|for)\s+(?:the\s+)?(?:deadline|due\s+date)\b", Opts)]
    private static partial Regex DeadlineChangeNoDateRegex();

    [GeneratedRegex(@"\b(?:delayed|delays?|slipping|slipped|slips|behind\s+schedule|running\s+(?:late|behind)|will\s+take\s+(?:a\s+(?:bit|little)\s+)?(?:longer|more\s+time|some\s+more\s+time|another)|taking\s+longer|take\s+(?:a\s+bit\s+)?longer|need\s+(?:some\s+|a\s+bit\s+)?more\s+time|needs\s+more\s+time|requires?\s+more\s+time|postpone[ds]?|postponing|pushed\s+(?:back|out)|push\s+(?:it\s+)?back|not\s+(?:be\s+)?able\s+to\s+(?:meet|make|finish|complete)|won't\s+(?:be\s+able\s+to\s+)?(?:make|meet|finish|complete)|won't\s+be\s+(?:done|ready|completed|finished|delivered)|will\s+not\s+be\s+(?:done|ready|completed|finished|delivered)|will\s+miss|miss(?:ed)?\s+the\s+deadline|later\s+than\s+(?:planned|expected)|overrun|over-run)\b", Opts)]
    private static partial Regex DelayRegex();

    [GeneratedRegex(@"\b(?:what\s+do\s+you\s+mean|please\s+clarify|could\s+you\s+clarify|can\s+you\s+clarify|need\s+(?:a\s+|some\s+)?clarifications?|need\s+more\s+(?:details|information|info|clarity)|not\s+clear|unclear|confused|confusing|which\s+(?:one|version|format|template|option|file)|do\s+you\s+want\s+(?:us|me)\s+to|is\s+(?:it|this)\s+(?:ok|okay|fine)\s+if|(?:could|can)\s+you\s+(?:confirm|explain|elaborate)|please\s+(?:confirm|explain|elaborate)|questions?\s+(?:about|on|regarding)|(?:what|which)\s+is\s+the\s+scope|scope\s+of\s+(?:work|the)|did\s+you\s+mean)\b", Opts)]
    private static partial Regex ClarificationRegex();

    [GeneratedRegex(@"\b(?:approved|i\s+approve|we\s+approve|go\s+ahead|looks\s+good,?\s+(?:please\s+)?proceed|lgtm|looks\s+good\s+to\s+me|signed\s+off|sign[\s-]?off\s+(?:given|done)|green\s+light|you\s+may\s+proceed|please\s+proceed|ok(?:ay)?\s+to\s+proceed|good\s+to\s+go|confirmed,?\s+(?:please\s+)?proceed|permission\s+granted)\b", Opts)]
    private static partial Regex ApprovalRegex();

    // Words before an approval phrase that turn it into something else: "pending approved…", "once approved",
    // "I will go ahead and deploy" (the assignee announcing, not the approver approving).
    [GeneratedRegex(@"(?:\b(?:pending|awaiting|waiting\s+for|need|needs|seeking|for|subject\s+to|once|if|when|after|until|yet\s+to\s+be|to\s+be|get|getting|not|will|shall|should|can|may|let\s+me|going\s+to|to)|'ll|’ll)\s+(?:\w+\s+)?$", Opts)]
    private static partial Regex ApprovalBlockerRegex();

    [GeneratedRegex(@"\b(?:if|any|avoid|prevent|in\s+case\s+of|without)\s+(?:\w+\s+){0,3}$", Opts)]
    private static partial Regex DelayGuardRegex();

    [GeneratedRegex(@"\b(?:not\s+approved|disapproved|rejected|reject(?:ing)?|redo|re-do|rework|re-work|not\s+acceptable|unacceptable|doesn't\s+meet|does\s+not\s+meet|not\s+satisfied|please\s+revise|needs?\s+(?:to\s+be\s+)?(?:revised|reworked|redone)|needs\s+changes|not\s+good\s+enough|declined|send\s+(?:it\s+)?back|start\s+over|not\s+what\s+(?:i|we)\s+(?:asked|wanted))\b", Opts)]
    private static partial Regex RejectionRegex();

    [GeneratedRegex(@"\b(?:escalat(?:e|ed|ing|ion)|need\s+(?:your\s+|urgent\s+)?(?:management\s+)?intervention|management\s+intervention|(?:customer|client)\s+is\s+(?:very\s+|extremely\s+)?(?:unhappy|angry|upset|frustrated|furious|dissatisfied)|(?:customer|client)\s+(?:has\s+)?(?:complained|is\s+complaining)|(?:customer|client)\s+complaint|threaten(?:ed|ing)?|legal\s+action|very\s+disappointed|seriously\s+concerned|loss\s+of\s+business|losing\s+the\s+(?:customer|client|account))\b", Opts)]
    private static partial Regex EscalationRegex();

    [GeneratedRegex(@"\b(?:need\s+(?:\w+\s+){0,2}help|need\s+(?:\w+\s+){0,2}assistance|need\s+(?:\w+\s+){0,2}support\s+(?:from|with|on)|(?:can|could)\s+(?:someone|anyone|you)\s+help|anyone\s+(?:who\s+)?can\s+help|please\s+help|help\s+needed|require\s+(?:\w+\s+)?(?:assistance|help|support)|need\s+guidance|need\s+inputs?\s+from|need\s+someone\s+to)\b", Opts)]
    private static partial Regex HelpRegex();

    [GeneratedRegex(@"\b(?<who>(?:the\s+)?[\p{L}\-]+(?:\s+[\p{L}\-]+)?)\s+(?:has|have|had)(?:n't|n’t|\s+not)\s+(?:yet\s+)?(?:shared|sent|provided|given|delivered|approved|replied|responded|confirmed|signed|released|paid)\b[^.;!?\n,]{0,80}|\b(?:have|has|had)?(?:n't|n’t|\s+not)\s+(?:yet\s+)?received\s+[^.;!?\n,]{3,80}", Opts)]
    private static partial Regex NotProvidedRegex();

    [GeneratedRegex(@"(?:n't|n’t|\bnot)\s+(?:yet\s+)?(?:been\s+)?(?:completed|done|finished|ready|closed|delivered)\b|\bnot\s+(?:yet\s+)?(?:complete|finished)\b", Opts)]
    private static partial Regex NotDoneRegex();

    [GeneratedRegex(@"\b(?:no\s+issues?\s+so\s+far|so\s+far\s+so\s+good|all\s+good|going\s+well|going\s+as\s+planned|on\s+it|will\s+(?:keep\s+you\s+)?(?:update|posted|informed)|will\s+share\s+(?:an?\s+)?update)\b", Opts)]
    private static partial Regex WeakProgressRegex();

    [GeneratedRegex(@"\b(?<pct>\d{1,3})\s*(?:%|percent|per\s+cent)\s*(?:done|complete|completed|finished|through|progress)?", Opts)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"\b(?:partially|partly)\s+(?:done|completed|complete|finished|delivered)\b", Opts)]
    private static partial Regex PartialRegex();

    [GeneratedRegex(@"\b(?:(?:have|has)\s+started|started\s+(?:on|working|the|with)|begun|commenced|kicked\s+off|picked\s+(?:it\s+|this\s+)?up|(?:am|are|is|currently|still)\s+working\s+on|in\s+progress|underway|under\s+way|progressing|making\s+(?:good\s+)?progress|on\s+track|halfway|half\s+way|almost\s+(?:done|complete|finished|there)|nearly\s+(?:done|complete|finished)|in\s+testing|under\s+review|in\s+review|status\s+update|progress\s+update)\b", Opts)]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"\b(?:fyi|for\s+your\s+(?:information|reference|info|records)|please\s+note|note\s+that|just\s+(?:to\s+)?(?:inform|let\s+you\s+know)|heads[\s-]up|sharing\s+(?:the|this|herewith)|attached|please\s+find|for\s+reference|update\s+you)\b", Opts)]
    private static partial Regex InformationRegex();

    [GeneratedRegex(@"\b(?:lunch|dinner|coffee|tea\s+break|birthday|party|cake|celebrat\w*|congratulations|congrats|happy\s+(?:diwali|holi|new\s+year|eid|christmas|birthday|anniversary|weekend|onam|pongal)|movie|cricket|festival|wedding|weekend\s+plans|team\s+outing|potluck)\b", Opts)]
    private static partial Regex SocialRegex();

    [GeneratedRegex(@"\b(?:urgent(?:ly)?|critical|asap|a\.s\.a\.p|immediately|emergency|right\s+away|top\s+priority|highest\s+priority|p1|sev\s*1|severity\s*1|showstopper|show-stopper|(?:customer|client|production|prod|server|system|site|website|portal|app|application)\s+(?:is\s+)?(?:down|not\s+working)|outage|production\s+issue)\b", Opts)]
    private static partial Regex UrgentRegex();

    [GeneratedRegex(@"(?:n't|n’t|\b(?:no|not|never|without|nothing|none|no\s+longer))\s+(?:\w+\s+){0,2}$", Opts)]
    private static partial Regex NegationBeforeRegex();
}
