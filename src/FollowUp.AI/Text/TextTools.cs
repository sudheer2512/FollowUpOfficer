using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FollowUp.AI.Text;

/// <summary>
/// Small, allocation-light text helpers shared by the heuristic engines: normalisation, tokenisation with a
/// light English stemmer, stop words, number words, sentence / clause splitting and edit distance.
/// All members are pure and thread-safe.
/// </summary>
internal static partial class TextTools
{
    /// <summary>Words that carry no meaning for matching or title building.</summary>
    public static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "if", "then", "else", "of", "to", "in", "on", "at", "by", "for", "with",
        "from", "into", "onto", "about", "as", "is", "are", "was", "were", "be", "been", "being", "am", "do", "does",
        "did", "doing", "have", "has", "had", "having", "i", "me", "my", "mine", "we", "us", "our", "ours", "you",
        "your", "yours", "he", "him", "his", "she", "her", "hers", "it", "its", "they", "them", "their", "theirs",
        "this", "that", "these", "those", "there", "here", "what", "which", "who", "whom", "whose", "when", "where",
        "why", "how", "all", "any", "both", "each", "few", "more", "most", "other", "some", "such", "no", "nor", "not",
        "only", "own", "same", "so", "than", "too", "very", "can", "will", "would", "should", "could", "shall", "may",
        "might", "must", "just", "also", "now", "up", "down", "out", "over", "under", "again", "once", "via", "per",
        "re", "fw", "fwd", "hi", "hello", "hey", "dear", "regards", "thanks", "thank", "please", "kindly", "sir",
        "madam", "team", "ll", "ve", "d", "s", "t", "m", "get", "got", "let", "know", "still", "yet", "already",
    };

    /// <summary>Generic work words: they match but carry little evidence that two texts are about the same task.</summary>
    public static readonly HashSet<string> WeakWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "task", "work", "updat", "status", "project", "report", "issue", "item", "request", "today", "tomorrow",
        "week", "month", "done", "complet", "finish", "pleas", "need", "send", "share", "check", "review", "follow",
        "new", "list", "detail", "plan", "data", "file", "doc", "document", "meet", "call", "mail", "email",
    };

    private static readonly Dictionary<string, int> NumberWordValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
        ["couple of"] = 2, ["a couple of"] = 2, ["couple"] = 2, ["few"] = 3, ["a few"] = 3, ["fifteen"] = 15,
        ["twenty"] = 20, ["thirty"] = 30,
    };

    /// <summary>Regex alternation of the supported number words (longest first), for embedding in patterns.</summary>
    public const string NumberWordPattern =
        @"\d{1,3}|a\s+couple\s+of|couple\s+of|a\s+few|few|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|twenty|thirty|an|a";

    /// <summary>Parses digits or a number word ("two", "a couple of"). Returns null when not a number.</summary>
    public static int? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = WhitespaceRegex().Replace(text.Trim(), " ");
        if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        return NumberWordValues.TryGetValue(t, out var v) ? v : null;
    }

    /// <summary>Collapses whitespace and trims.</summary>
    public static string Squash(string? text) => string.IsNullOrEmpty(text) ? "" : WhitespaceRegex().Replace(text, " ").Trim();

    /// <summary>Lower-case alphanumerics only ("e-Invoicing" → "einvoicing"); used for compact alias comparison.</summary>
    public static string Compact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Lower-cased words (letters / digits) in order of appearance, without stemming or stop-word removal.</summary>
    public static IEnumerable<string> Words(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in WordRegex().Matches(text)) yield return m.Value.ToLowerInvariant();
    }

    /// <summary>Content tokens: lower-cased, stemmed, stop words and 1-letter words removed.</summary>
    public static List<string> ContentTokens(string? text)
    {
        var list = new List<string>();
        foreach (var w in Words(text))
        {
            if (w.Length < 2 && !char.IsDigit(w[0])) continue;
            if (StopWords.Contains(w)) continue;
            var s = Stem(w);
            if (s.Length == 0 || StopWords.Contains(s)) continue;
            list.Add(s);
        }
        return list;
    }

    /// <summary>
    /// A deliberately light English stemmer (plural, -ing, -ed, -ation, trailing e) so that "screens" / "screen",
    /// "invoicing" / "invoice" and "integration" / "integrate" share a stem.
    /// </summary>
    public static string Stem(string word)
    {
        var w = word.ToLowerInvariant();
        if (w.Length <= 3 || w.Any(char.IsDigit)) return w;
        if (w.EndsWith("'s", StringComparison.Ordinal)) w = w[..^2];
        if (w.EndsWith("ies", StringComparison.Ordinal) && w.Length > 4) w = w[..^3] + "y";
        else if (w.EndsWith("sses", StringComparison.Ordinal)) w = w[..^2];
        else if (w.EndsWith("es", StringComparison.Ordinal) && w.Length > 4 && (w.EndsWith("ches", StringComparison.Ordinal) || w.EndsWith("shes", StringComparison.Ordinal) || w.EndsWith("xes", StringComparison.Ordinal) || w.EndsWith("zes", StringComparison.Ordinal))) w = w[..^2];
        else if (w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) && !w.EndsWith("us", StringComparison.Ordinal) && !w.EndsWith("is", StringComparison.Ordinal) && w.Length > 3) w = w[..^1];

        if (w.EndsWith("ations", StringComparison.Ordinal)) w = w[..^6] + "ate";
        else if (w.EndsWith("ation", StringComparison.Ordinal) && w.Length > 6) w = w[..^5] + "ate";
        else if (w.EndsWith("ing", StringComparison.Ordinal) && w.Length > 5) w = UnDouble(w[..^3]);
        else if (w.EndsWith("ed", StringComparison.Ordinal) && w.Length > 4 && !w.EndsWith("eed", StringComparison.Ordinal)) w = UnDouble(w[..^2]);
        else if (w.EndsWith("ment", StringComparison.Ordinal) && w.Length > 7) w = w[..^4];

        if (w.EndsWith('e') && w.Length > 4) w = w[..^1];
        return w;

        static string UnDouble(string s) =>
            s.Length > 3 && s[^1] == s[^2] && !"aeiouls".Contains(s[^1]) ? s[..^1] : s;
    }

    /// <summary>Splits text into sentences on . ! ? and line breaks, keeping the start offset of each sentence.</summary>
    public static List<(int Start, string Text)> Sentences(string? text)
    {
        var result = new List<(int, string)>();
        if (string.IsNullOrEmpty(text)) return result;
        var start = 0;
        foreach (Match m in SentenceBreakRegex().Matches(text))
        {
            Add(start, m.Index + m.Length);
            start = m.Index + m.Length;
        }
        Add(start, text.Length);
        return result;

        void Add(int from, int to)
        {
            if (to <= from) return;
            var raw = text.Substring(from, to - from);
            var lead = raw.Length - raw.TrimStart().Length;
            var trimmed = raw.Trim();
            if (trimmed.Length > 0) result.Add((from + lead, trimmed));
        }
    }

    /// <summary>
    /// Optimal-string-alignment distance (Levenshtein plus adjacent transposition, so "Rahlu" → "Rahul" is one edit).
    /// Returns <paramref name="max"/> + 1 as soon as the distance is known to exceed <paramref name="max"/>.
    /// </summary>
    public static int EditDistance(string a, string b, int max = int.MaxValue)
    {
        if (a == b) return 0;
        if (Math.Abs(a.Length - b.Length) > max) return max == int.MaxValue ? Math.Abs(a.Length - b.Length) : max + 1;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var rowMin = int.MaxValue;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[i - 2, j - 2] + 1);
                d[i, j] = v;
                rowMin = Math.Min(rowMin, v);
            }
            if (max != int.MaxValue && rowMin > max) return max + 1;
        }
        return d[a.Length, b.Length];
    }

    /// <summary>True when the two words are equal, or both are at least 5 characters long and differ by one edit.</summary>
    public static bool FuzzyEquals(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Length < 5 || b.Length < 5) return false;
        return EditDistance(a.ToLowerInvariant(), b.ToLowerInvariant(), 1) <= 1;
    }

    /// <summary>Upper-cases the first letter.</summary>
    public static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];

    /// <summary>Truncates to <paramref name="max"/> characters on a word boundary, adding an ellipsis.</summary>
    public static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        var cut = s.LastIndexOf(' ', Math.Max(0, max - 1));
        return (cut > max / 2 ? s[..cut] : s[..max]).TrimEnd() + "…";
    }

    /// <summary>Removes quoted reply history (lines starting with "&gt;", "On … wrote:", "-----Original Message-----", "From:" headers).</summary>
    public static string StripQuotedReply(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var m = ReplyHeaderRegex().Match(body);
        var text = m.Success && m.Index > 0 ? body[..m.Index] : body;
        var lines = text.Split('\n').Where(l => !l.TrimStart().StartsWith('>'));
        return string.Join('\n', lines).Trim();
    }

    /// <summary>Formats a date the way the UI shows it: "Fri 9 Oct 2026".</summary>
    public static string Show(DateOnly d) => d.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>Formats a time as HH:mm.</summary>
    public static string Show(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:'[\p{L}]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    // A sentence ends at . ! ? followed by whitespace (not inside "5.30" or "e.g.") or at a line break.
    [GeneratedRegex(@"(?<=[.!?])(?<!\b(?:e\.g|i\.e|etc|vs|approx|mr|mrs|ms|dr|a\.m|p\.m)\.)\s+(?=\S)|\r?\n+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SentenceBreakRegex();

    [GeneratedRegex(@"^\s*(?:-{2,}\s*Original Message\s*-{2,}|On\s.{4,200}?wrote:|From:\s.+\r?\n\s*(?:Sent|Date):)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ReplyHeaderRegex();
}
