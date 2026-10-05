using System.Globalization;
using System.Text.RegularExpressions;
using FollowUp.AI.Dates;
using FollowUp.AI.Text;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Domain.Rules;

namespace FollowUp.AI.Heuristics;

/// <summary>
/// Deterministic semantic matcher: scores each <see cref="TaskCandidate"/> for a message from token overlap (light
/// stemming, stop words, distinctiveness across the candidate set), the sender being an assignee, dates near the due
/// date and an explicit task number, then combines the result with Core's deterministic <see cref="TaskCandidate.PriorScore"/>.
/// </summary>
/// <remarks>
/// Evidence (additive, capped at 0.97): title coverage 0.55 × coverage + 0.06 per distinctive matched title word (max 4);
/// keywords 0.12 each (max 0.25); project 0.1 × fraction of project words; description 0.02 per shared word (max 5);
/// sender is an assignee +0.2 (an assignee among participants +0.05); a date within two days of the due date +0.05; the
/// task number in the text lifts the score to 0.97. Closed tasks are damped ×0.8. Final = 1 − (1 − prior)(1 − semantic)
/// (independent evidence, noisy-OR). Calibration: sender-is-assignee plus three distinctive title words ≥ 0.85; a single
/// shared generic word ≤ 0.5. Stateless and thread-safe.
/// </remarks>
public sealed partial class HeuristicTaskMatcher
{
    /// <summary>Scores every candidate (highest first).</summary>
    public IReadOnlyList<CandidateScore> Score(MessageForAnalysis message, IReadOnlyList<TaskCandidate> candidates, BusinessCalendar? calendar = null)
    {
        if (candidates.Count == 0) return [];
        var cal = calendar ?? BusinessCalendar.Default;
        var subject = SubjectPrefixRegex().Replace(message.Subject ?? "", "");
        var body = TextTools.StripQuotedReply(message.Body);
        var subjectTokens = TextTools.ContentTokens(subject).ToHashSet();
        var bodyTokens = TextTools.ContentTokens(body).ToHashSet();
        var allTokens = subjectTokens.Union(bodyTokens).ToHashSet();
        var numbers = TaskNumbers.FindAll(subject + " " + body);
        var dates = DateExpressionParser.Parse(subject + "\n" + body, message.SentAtUtc, cal).Select(d => d.Date).ToList();

        // Distinctiveness: a word that appears in many candidate titles says little about which task is meant.
        var df = new Dictionary<string, int>();
        foreach (var c in candidates)
            foreach (var t in TextTools.ContentTokens(c.Title + " " + string.Join(' ', c.Keywords)).Distinct())
                df[t] = df.GetValueOrDefault(t) + 1;
        var n = candidates.Count;
        double Weight(string t)
        {
            var idf = n >= 3 ? Math.Clamp(1.0 - 0.5 * (df.GetValueOrDefault(t, 1) - 1) / (n - 1), 0.5, 1.0) : 1.0;
            return (TextTools.WeakWords.Contains(t) ? 0.35 : 1.0) * idf;
        }

        var results = new List<CandidateScore>(candidates.Count);
        foreach (var c in candidates)
        {
            var signals = new List<string>();
            var raw = 0.0;

            // Title (highest weight).
            var title = StemMap(c.Title);
            var totalW = title.Keys.Sum(Weight);
            var inSubject = title.Keys.Where(subjectTokens.Contains).ToList();
            var inBody = title.Keys.Where(t => !subjectTokens.Contains(t) && bodyTokens.Contains(t)).ToList();
            if (totalW > 0)
            {
                var matchedW = inSubject.Sum(Weight) + 0.85 * inBody.Sum(Weight);
                var coverage = Math.Min(1, matchedW / totalW);
                var distinctive = inSubject.Concat(inBody).Count(t => !TextTools.WeakWords.Contains(t));
                raw += 0.55 * coverage + 0.06 * Math.Min(distinctive, 4);
            }
            if (inSubject.Count > 0) signals.Add($"subject matches title words: {string.Join(", ", inSubject.Select(t => title[t]))}");
            if (inBody.Count > 0) signals.Add($"body matches title words: {string.Join(", ", inBody.Select(t => title[t]))}");

            // Keywords.
            var keywordHits = c.Keywords
                .Where(k => TextTools.ContentTokens(k) is { Count: > 0 } kt && kt.All(allTokens.Contains))
                .ToList();
            if (keywordHits.Count > 0)
            {
                raw += Math.Min(0.25, 0.12 * keywordHits.Count);
                signals.Add($"keywords matched: {string.Join(", ", keywordHits)}");
            }

            // Project.
            var projectTokens = TextTools.ContentTokens(c.ProjectName).Where(t => !TextTools.WeakWords.Contains(t)).Distinct().ToList();
            if (projectTokens.Count > 0)
            {
                var frac = projectTokens.Count(allTokens.Contains) / (double)projectTokens.Count;
                if (frac >= 0.5)
                {
                    raw += 0.1 * frac;
                    signals.Add($"project mentioned: {c.ProjectName}");
                }
            }

            // Description (lowest weight).
            var descShared = TextTools.ContentTokens(c.Description)
                .Where(t => !TextTools.WeakWords.Contains(t) && !title.ContainsKey(t) && allTokens.Contains(t))
                .Distinct().ToList();
            if (descShared.Count > 0)
            {
                raw += 0.02 * Math.Min(descShared.Count, 5);
                if (descShared.Count >= 2) signals.Add($"description overlap: {string.Join(", ", descShared.Take(5))}");
            }

            // People.
            if (IsAssignee(message.SenderName, c.AssigneeNames))
            {
                raw += 0.2;
                signals.Add("sender is the assignee");
            }
            else if (message.ParticipantNames.Any(p => IsAssignee(p, c.AssigneeNames)))
            {
                raw += 0.05;
                signals.Add("an assignee is on the conversation");
            }

            // Dates near the due date.
            if (c.DueDate is { } due && dates.FirstOrDefault(d => Math.Abs(d.DayNumber - due.DayNumber) <= 2) is var near && near != default)
            {
                raw += 0.05;
                signals.Add($"mentions {TextTools.Show(near)}, near the due date {TextTools.Show(due)}");
            }

            if (numbers.Contains(c.Number, StringComparer.OrdinalIgnoreCase))
            {
                raw = Math.Max(raw, 0.97);
                signals.Add($"task number {c.Number} mentioned");
            }

            if (c.Status is WorkStatus.Completed or WorkStatus.Cancelled)
            {
                raw *= 0.8;
                signals.Add($"task is {c.Status.ToString().ToLowerInvariant()}");
            }

            var semantic = Math.Clamp(raw, 0, 0.97);
            var prior = double.IsFinite(c.PriorScore) ? Math.Clamp(c.PriorScore, 0, 1) : 0;
            var confidence = Math.Round(Math.Clamp(1 - (1 - prior) * (1 - semantic), 0, 0.99), 3);

            var allSignals = c.PriorSignals.Concat(signals).Distinct().ToList();
            results.Add(new CandidateScore(c.TaskId, confidence, Explain(confidence, prior, semantic, signals), allSignals));
        }
        return results.OrderByDescending(r => r.Confidence).ToList();
    }

    /// <summary>True when <paramref name="name"/> (a display name or an email address) is one of the assignees.</summary>
    internal static bool IsAssignee(string? name, IReadOnlyList<string> assignees)
    {
        if (string.IsNullOrWhiteSpace(name) || assignees.Count == 0) return false;
        var raw = name.Trim();
        var at = raw.IndexOf('@', StringComparison.Ordinal);
        var compact = TextTools.Compact(at > 0 ? raw[..at] : raw);
        var words = TextTools.Words(at > 0 ? raw[..at].Replace('.', ' ').Replace('_', ' ') : raw).ToList();
        foreach (var a in assignees)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            if (TextTools.Compact(a) == compact) return true;
            var aw = TextTools.Words(a).ToList();
            // "Rahul Sharma" vs "Rahul K Sharma": first and last names agree.
            if (words.Count >= 2 && aw.Count >= 2 && words[0] == aw[0] && words[^1] == aw[^1]) return true;
        }
        return false;
    }

    private static Dictionary<string, string> StemMap(string? text)
    {
        var map = new Dictionary<string, string>();
        foreach (var w in TextTools.Words(text))
        {
            if ((w.Length < 2 && !char.IsDigit(w[0])) || TextTools.StopWords.Contains(w)) continue;
            var s = TextTools.Stem(w);
            if (s.Length > 0 && !TextTools.StopWords.Contains(s)) map.TryAdd(s, w);
        }
        return map;
    }

    private static string Explain(double confidence, double prior, double semantic, List<string> signals)
    {
        var strength = confidence >= 0.85 ? "Strong match" : confidence >= 0.6 ? "Likely match" : confidence >= 0.35 ? "Possible match" : "Weak match";
        var parts = signals.Take(4).ToList();
        var text = parts.Count > 0 ? $"{strength}: {string.Join("; ", parts)}." : $"{strength}: little in common with the task.";
        if (prior > 0) text += $" Deterministic pre-score {prior.ToString("0.00", CultureInfo.InvariantCulture)} combined with text score {semantic.ToString("0.00", CultureInfo.InvariantCulture)}.";
        return text;
    }

    [GeneratedRegex(@"^\s*(?:(?:re|fw|fwd|aw|sv)\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubjectPrefixRegex();
}
