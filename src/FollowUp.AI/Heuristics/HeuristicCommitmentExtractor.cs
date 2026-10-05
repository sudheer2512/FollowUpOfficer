using System.Text.RegularExpressions;
using FollowUp.AI.Dates;
using FollowUp.AI.Text;
using FollowUp.Core.Abstractions;
using FollowUp.Domain.Rules;

namespace FollowUp.AI.Heuristics;

/// <summary>
/// Deterministic detector of promises made by the <em>writer</em> of a message ("We will complete this by Friday",
/// "I'll send it by tomorrow EOD", "ETA Monday", "target date is 20th").
/// </summary>
/// <remarks>
/// <para>Each sentence is split into clauses (on ";", "but", "however", "instead"…); a clause yields a commitment when it
/// contains a future date and a commitment cue. Cue strength: first-person "will" / "'ll" 0.9; "committed for", ETA,
/// "will be done" 0.85; "expect to", "target date" 0.8; "should be done", "we should" 0.75; "we can" 0.7; elliptical
/// "Done by Friday" 0.6; "we could / might" 0.5. Confidence = strength × (0.7 + 0.3 × date confidence).</para>
/// <para>Excluded: questions, requests to the reader ("please complete by Friday", "can you…"), negations ("won't be done
/// by Friday") and promises by third parties ("the vendor will send…"). Conditions ("if we get credentials…", "once…")
/// multiply confidence by 0.55; hedges ("try to", "hopefully") by 0.75. Results under 0.45 are dropped.</para>
/// Stateless and thread-safe.
/// </remarks>
public sealed partial class HeuristicCommitmentExtractor : ICommitmentExtractionService
{
    /// <summary>Commitments with a lower confidence are not reported.</summary>
    public const double MinConfidence = 0.45;

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly (Func<Regex> Pattern, double Strength, bool Passive)[] Cues =
    [
        (FirstPersonWillRegex, 0.9, false),
        (FirstPersonGoingToRegex, 0.85, false),
        (FirstPersonPlanRegex, 0.85, false),
        (CommittedRegex, 0.85, false),
        (EtaRegex, 0.85, false),
        (WillBeDoneRegex, 0.85, true),
        (ElidedWillRegex, 0.8, false),
        (ExpectRegex, 0.8, false),
        (TargetRegex, 0.8, false),
        (FirstPersonShouldRegex, 0.75, false),
        (ShouldBeDoneRegex, 0.75, true),
        (OnTrackRegex, 0.75, false),
        (FirstPersonCanRegex, 0.7, false),
        (PlannedForRegex, 0.7, false),
        (FirstPersonHopeRegex, 0.6, false),
        (ElidedDoneRegex, 0.6, true),
        (FirstPersonCouldRegex, 0.5, false),
    ];

    /// <inheritdoc />
    public Task<IReadOnlyList<ExtractedCommitment>> ExtractAsync(string text, DateTime sentAtUtc, BusinessCalendar calendar, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Extract(text, sentAtUtc, calendar));
    }

    /// <summary>Synchronous core of <see cref="ExtractAsync"/>.</summary>
    public IReadOnlyList<ExtractedCommitment> Extract(string? text, DateTime sentAtUtc, BusinessCalendar calendar)
    {
        var body = TextTools.StripQuotedReply(text);
        if (body.Length == 0) return [];
        var today = calendar.Today(sentAtUtc);
        var results = new List<(int Position, ExtractedCommitment Commitment)>();

        foreach (var (sentenceStart, sentence) in TextTools.Sentences(body))
        {
            if (IsQuestion(sentence)) continue;
            foreach (var (clauseStart, clause) in Clauses(sentence))
            {
                var dates = DateExpressionParser.Parse(clause, sentAtUtc, calendar).Where(d => d.Date >= today).ToList();
                if (dates.Count == 0) continue;
                var c = Evaluate(clause, dates);
                if (c is not null) results.Add((sentenceStart + clauseStart, c));
            }
        }

        return results
            .GroupBy(r => (r.Commitment.Date, r.Commitment.Time))
            .Select(g => g.OrderByDescending(r => r.Commitment.Confidence).First())
            .OrderBy(r => r.Position)
            .Select(r => r.Commitment)
            .ToList();
    }

    private static ExtractedCommitment? Evaluate(string clause, List<DateMatch> dates)
    {
        // Requests to the reader, and requests for more time ("we may need till 25 Oct") are not promises.
        if (RequestRegex().IsMatch(clause) || ExtensionRequestRegex().IsMatch(clause)) return null;

        Match? cue = null;
        var strength = 0.0;
        var passive = false;
        foreach (var (pattern, s, isPassive) in Cues)
        {
            var m = pattern().Match(clause);
            if (m.Success && s > strength)
            {
                cue = m;
                strength = s;
                passive = isPassive;
            }
        }
        if (cue is null) return null;

        var date = ChooseDate(clause, dates, cue);

        // Negation near the promise ("we will not finish by Friday", "won't be done") cancels it; a negation earlier in
        // the sentence about something else ("this is not a problem, we will…") does not.
        var lastComma = clause.LastIndexOf(',', Math.Max(0, cue.Index - 1));
        var windowStart = Math.Max(Math.Max(0, cue.Index - 25), lastComma >= 0 && lastComma < cue.Index ? lastComma + 1 : 0);
        windowStart = Math.Min(windowStart, date.Index);
        var windowEnd = Math.Min(clause.Length, Math.Max(date.End, cue.Index + cue.Length));
        if (NegationRegex().IsMatch(clause[windowStart..windowEnd])) return null;

        // Promises by someone else ("the vendor will send…") are facts about them, not commitments by the writer.
        if (ThirdPartyRegex().IsMatch(clause) && !FirstPersonRegex().IsMatch(clause)) strength = Math.Min(strength, 0.4);
        if (ConditionRegex().IsMatch(clause)) strength *= 0.55;
        if (HedgeRegex().IsMatch(clause)) strength *= 0.75;

        var confidence = Math.Round(Math.Clamp(strength * (0.7 + 0.3 * date.Confidence), 0, 1), 2);
        if (confidence < MinConfidence) return null;

        return new ExtractedCommitment(date.Date, date.Time, TextTools.Squash(clause).TrimEnd('.', '!', ','), ExtractWhat(clause, cue, date, passive), confidence);
    }

    /// <summary>
    /// The promised date: after the cue, the first date introduced by "by / before / until…", else the first date after
    /// the cue; when every date precedes the cue ("By Friday, we will…"), the last such date.
    /// </summary>
    private static DateMatch ChooseDate(string clause, List<DateMatch> dates, Match cue)
    {
        var cueEnd = cue.Index + cue.Length;
        var after = dates.Where(d => d.Index >= cueEnd).ToList();
        if (after.Count > 0)
            return after.FirstOrDefault(d => DeadlinePrepositionRegex().IsMatch(clause[..d.Index])) ?? after[0];
        return dates.LastOrDefault(d => DeadlinePrepositionRegex().IsMatch(clause[..d.Index])) ?? dates[^1];
    }

    private static string? ExtractWhat(string clause, Match cue, DateMatch date, bool passive)
    {
        if (passive)
        {
            var subject = TextTools.Squash(clause[..cue.Index]);
            subject = LeadingConnectorRegex().Replace(subject, "").Trim(' ', ',');
            return subject.Length is > 2 and <= 80 && !PronounOnlyRegex().IsMatch(subject) ? subject : null;
        }

        var from = cue.Index + cue.Length;
        if (date.Index <= from) return null;
        var what = TextTools.Squash(clause[from..date.Index]);
        what = LeadingFillerRegex().Replace(what, "");
        for (var i = 0; i < 3; i++) what = TrailingPrepositionRegex().Replace(what, "").Trim(' ', ',', ':', '-');
        return what.Length is >= 2 and <= 100 ? what : null;
    }

    private static bool IsQuestion(string sentence) =>
        sentence.TrimEnd().EndsWith('?') || QuestionStartRegex().IsMatch(sentence);

    private static IEnumerable<(int Start, string Text)> Clauses(string sentence)
    {
        var start = 0;
        foreach (Match m in ClauseBreakRegex().Matches(sentence))
        {
            if (m.Index > start) yield return (start, sentence[start..m.Index]);
            start = m.Index + m.Length;
        }
        if (start < sentence.Length) yield return (start, sentence[start..]);
    }

    // ───────────────────────────── cue patterns ─────────────────────────────

    [GeneratedRegex(@"\b(?:i|we)\s*(?:'ll|’ll|\s+will|\s+shall)\b(?:\s+(?:definitely|surely|certainly|also|then))?", Opts)]
    private static partial Regex FirstPersonWillRegex();

    [GeneratedRegex(@"\b(?:i|we)\s*(?:'m|’m|'re|’re|\s+am|\s+are)\s+(?:going\s+to|planning\s+to|aiming\s+to|committed\s+to|targeting|confident\s+(?:of|that\s+we\s+(?:will|can))|on\s+track\s+to)\b", Opts)]
    private static partial Regex FirstPersonGoingToRegex();

    [GeneratedRegex(@"\b(?:i|we)\s+(?:plan|aim|intend|commit|promise|undertake|expect)\s+to\b", Opts)]
    private static partial Regex FirstPersonPlanRegex();

    [GeneratedRegex(@"\b(?:committed|commit|committing)\s+(?:for|to|by)\b|\bcommitment\s+(?:is|date\s+is)\b", Opts)]
    private static partial Regex CommittedRegex();

    [GeneratedRegex(@"\beta\b\s*(?:is|:|-|=|for\s+[^,;]{1,60}?\s+is)?", Opts)]
    private static partial Regex EtaRegex();

    [GeneratedRegex(@"\b(?:will|'ll|’ll|shall)\s+be\s+(?:fully\s+|all\s+)?(?:done|completed|complete|finished|ready|delivered|deployed|closed|shared|sent|submitted|live|resolved|fixed|available|uploaded|in\s+place|handed\s+over|signed\s+off)\b", Opts)]
    private static partial Regex WillBeDoneRegex();

    [GeneratedRegex(@"^\s*(?:and\s+|so\s+|also\s+)?(?:will|shall)\s+(?!you\b|they\b|he\b|she\b|it\s+be\b)", Opts)]
    private static partial Regex ElidedWillRegex();

    [GeneratedRegex(@"\b(?:expect(?:ed|ing)?|hoping)\s+to\b", Opts)]
    private static partial Regex ExpectRegex();

    [GeneratedRegex(@"\btarget(?:ed)?\s*(?:completion\s+)?(?:date\s*)?(?:is\b|of\b|for\b|:|=)|\btargeting\b", Opts)]
    private static partial Regex TargetRegex();

    [GeneratedRegex(@"\b(?:i|we)\s+should\b(?:\s+be\s+able\s+to)?", Opts)]
    private static partial Regex FirstPersonShouldRegex();

    [GeneratedRegex(@"\bshould\s+be\s+(?:fully\s+|all\s+)?(?:done|completed|complete|finished|ready|delivered|deployed|closed|shared|sent|submitted|live|resolved|fixed|available|uploaded|in\s+place)\b", Opts)]
    private static partial Regex ShouldBeDoneRegex();

    [GeneratedRegex(@"\bon\s+track\s+(?:for|to)\b", Opts)]
    private static partial Regex OnTrackRegex();

    [GeneratedRegex(@"\b(?:i|we)\s+can\b(?:\s+(?:definitely|surely))?", Opts)]
    private static partial Regex FirstPersonCanRegex();

    [GeneratedRegex(@"\b(?:planned|scheduled)\s+(?:for|by|on)\b", Opts)]
    private static partial Regex PlannedForRegex();

    [GeneratedRegex(@"\b(?:i|we)\s+hope\s+to\b", Opts)]
    private static partial Regex FirstPersonHopeRegex();

    [GeneratedRegex(@"^\s*(?:done|ready|completed|complete|delivery)\s+(?=by\b|on\b|before\b)", Opts)]
    private static partial Regex ElidedDoneRegex();

    [GeneratedRegex(@"\b(?:i|we)\s+(?:could|might|may)\b(?:\s+be\s+able\s+to)?", Opts)]
    private static partial Regex FirstPersonCouldRegex();

    // ───────────────────────────── exclusions and modifiers ─────────────────────────────

    [GeneratedRegex(@"\b(?:please(?!\s+(?:note|find|be\s+(?:informed|advised)|see))|pls|kindly|can\s+you|could\s+you|would\s+you|will\s+you|you\s+(?:need|have|must|should|are\s+required)\s+to|you\s+must|you\s+should|make\s+sure|ensure\s+(?:that|it|this|the)|i\s+want\s+you|i\s+need\s+you|we\s+need\s+you|request\s+you|let\s+me\s+know\s+(?:if|whether))\b", Opts)]
    private static partial Regex RequestRegex();

    [GeneratedRegex(@"\b(?:need|needs|require|requires|request|requesting|want)\s+(?:an?\s+|some\s+|more\s+)?(?:extension|more\s+time|time\s+(?:till|until|up\s*to)|till|until|up\s*to)\b|\b(?:move|extend|push|postpone|reschedule)\s+(?:the\s+)?(?:deadline|due\s+date|target\s+date)\b", Opts)]
    private static partial Regex ExtensionRequestRegex();

    [GeneratedRegex(@"\b(?:won't|won’t|wont|will\s+not|shan't|shall\s+not|can't|can’t|cannot|can\s+not|couldn't|could\s+not|not\s+be\s+able|unable|not\s+going\s+to|isn't|is\s+not|aren't|are\s+not|not\s+possible|no\s+way|not\s+(?:be\s+)?(?:done|ready|completed|finished)|never)\b", Opts)]
    private static partial Regex NegationRegex();

    [GeneratedRegex(@"\b(?:he|she|they|the\s+(?:vendor|customer|client|supplier|provider|bank|government|department|partner|consultant|agency|auditor)s?|vendor|customer|client|supplier|provider)\s+(?:will|'ll|’ll|should|shall|is\s+going\s+to|are\s+going\s+to|plans?\s+to|expects?\s+to|has\s+promised|promised)\b", Opts)]
    private static partial Regex ThirdPartyRegex();

    [GeneratedRegex(@"\b(?:i|we|our\s+team|us)\b", Opts)]
    private static partial Regex FirstPersonRegex();

    [GeneratedRegex(@"\b(?:if|unless|provided(?:\s+that)?|assuming|once|as\s+soon\s+as|subject\s+to|depending\s+on|in\s+case|only\s+if|should\s+we\s+get)\b", Opts)]
    private static partial Regex ConditionRegex();

    [GeneratedRegex(@"\b(?:try\s+(?:to|and|our\s+best)|hopefully|probably|maybe|possibly|tentatively|approximately|roughly|ideally)\b", Opts)]
    private static partial Regex HedgeRegex();

    [GeneratedRegex(@"\b(?:by|before|till|until|no\s+later\s+than|latest\s+by|not\s+later\s+than)\s+(?:the\s+)?(?:end\s+of\s+)?$", Opts)]
    private static partial Regex DeadlinePrepositionRegex();

    [GeneratedRegex(@"^(?:\s*(?:be\s+able\s+to|try\s+to|try\s+and|get\s+to|definitely|surely|also|then|for|of|on|is|are|:|-)\s+)+", Opts)]
    private static partial Regex LeadingFillerRegex();

    [GeneratedRegex(@"\s*\b(?:by|on|before|until|till|within|in|at|for|around|latest|no\s+later\s+than|not\s+later\s+than|the|is|are|will\s+be|=)\s*$", Opts)]
    private static partial Regex TrailingPrepositionRegex();

    [GeneratedRegex(@"^(?:and|so|also|then|but)\s+", Opts)]
    private static partial Regex LeadingConnectorRegex();

    [GeneratedRegex(@"^(?:it|this|that|everything|all|all\s+of\s+it|work|the\s+work|things)$", Opts)]
    private static partial Regex PronounOnlyRegex();

    [GeneratedRegex(@"^\s*(?:can|could|would|will|shall|should|is|are|do|does|did|when|what|whether|have|has)\s+(?:you|we|it|they|this|that|the|he|she)\b", Opts)]
    private static partial Regex QuestionStartRegex();

    [GeneratedRegex(@"\s*;\s*|,?\s+but\s+|,?\s+however,?\s+|,?\s+instead,?\s+|\s+[—–]\s+|\s+-\s+", Opts)]
    private static partial Regex ClauseBreakRegex();
}
