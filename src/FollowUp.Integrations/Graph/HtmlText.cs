using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FollowUp.Integrations.Graph;

/// <summary>
/// Converts Outlook / Teams HTML bodies to plain text and strips quoted reply history (best effort), so AI
/// classification and correlation only see what the sender actually wrote.
/// </summary>
public static partial class HtmlText
{
    /// <summary>Email body (HTML or text) to plain text without the quoted history of earlier messages.</summary>
    public static string MailBodyToText(string? content, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        var isHtml = IsHtml(content, contentType);
        var text = isHtml ? ToPlainText(CutHtmlQuote(content)) : Normalize(content);
        return StripQuotedReply(text);
    }

    /// <summary>Teams message body to plain text: mentions become "@Name", reply quotes and attachment placeholders are dropped.</summary>
    public static string TeamsBodyToText(string? content, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        if (!IsHtml(content, contentType)) return Normalize(content);
        var html = BlockquoteRegex().Replace(content, "");
        html = AttachmentTagRegex().Replace(html, "");
        return ToPlainText(html);
    }

    /// <summary>HTML to readable plain text (block elements become line breaks, entities decoded, whitespace normalised).</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = CommentRegex().Replace(html, "");
        s = InvisibleBlockRegex().Replace(s, "");
        s = MentionRegex().Replace(s, "@$1");
        s = LineBreakRegex().Replace(s, "\n");
        s = BlockEndRegex().Replace(s, "\n");
        s = ListItemRegex().Replace(s, "\n- ");
        s = CellRegex().Replace(s, "\t");
        s = TagRegex().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        return Normalize(s);
    }

    /// <summary>
    /// Cuts the text at the first line that starts quoted history: "-----Original Message-----", an Outlook
    /// "From: … Sent:/To:/Subject:" header block, "On … wrote:", an underscore separator or a trailing "&gt;" quote block.
    /// Returns the original text when the cut would leave nothing.
    /// </summary>
    public static string StripQuotedReply(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var cut = lines.Length;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (OriginalMessageRegex().IsMatch(line)) { cut = i; break; }
            if (UnderscoreRegex().IsMatch(line) && NextNonEmptyStartsWithHeader(lines, i + 1)) { cut = i; break; }
            if (i > 0 && FromHeaderRegex().IsMatch(line) && HeaderBlockFollows(lines, i + 1)) { cut = i; break; }
            if (WroteRegex().IsMatch(line)) { cut = i; break; }
            if (line.StartsWith("On ", StringComparison.Ordinal) && i + 1 < lines.Length
                && lines[i + 1].Trim().EndsWith("wrote:", StringComparison.OrdinalIgnoreCase) && line.Length < 250) { cut = i; break; }
            if (line.StartsWith('>') && lines.Skip(i).All(l => l.Trim().Length == 0 || l.TrimStart().StartsWith('>'))) { cut = i; break; }
        }
        var result = string.Join('\n', lines.Take(cut)).Trim();
        return result.Length == 0 ? Normalize(text) : result;
    }

    private static bool IsHtml(string content, string? contentType) =>
        string.Equals(contentType, "html", StringComparison.OrdinalIgnoreCase)
        || (contentType is null && AnyTagRegex().IsMatch(content));

    /// <summary>Cuts an HTML body at well-known reply/forward containers (Outlook, Gmail, generic blockquote).</summary>
    private static string CutHtmlQuote(string html)
    {
        var cut = -1;
        foreach (var marker in QuoteMarkers)
        {
            var index = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 0 && (cut < 0 || index < cut)) cut = index;
        }
        if (cut < 0) return html;
        // Back up to the start of the tag carrying the marker attribute.
        var tagStart = html.LastIndexOf('<', cut);
        if (tagStart >= 0) cut = tagStart;
        if (cut == 0) return html;
        // Outlook puts an <hr> right before divRplyFwdMsg; include it in the cut.
        var hr = html.LastIndexOf("<hr", cut - 1, StringComparison.OrdinalIgnoreCase);
        if (hr >= 0 && cut - hr < 200) cut = hr;
        var head = html[..cut];
        return ToPlainText(head).Length == 0 ? html : head;
    }

    private static readonly string[] QuoteMarkers =
    [
        "id=\"divRplyFwdMsg\"", "id=\"appendonsend\"", "class=\"gmail_quote\"", "id=\"mail-editor-reference-message-container\"", "<blockquote",
    ];

    private static bool NextNonEmptyStartsWithHeader(string[] lines, int from)
    {
        for (var i = from; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0) continue;
            return FromHeaderRegex().IsMatch(l);
        }
        return false;
    }

    private static bool HeaderBlockFollows(string[] lines, int from)
    {
        for (var i = from; i < Math.Min(lines.Length, from + 5); i++)
            if (HeaderLineRegex().IsMatch(lines[i].Trim())) return true;
        return false;
    }

    /// <summary>Trims lines, collapses runs of spaces and of blank lines.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = SpacesRegex().Replace(raw.Replace(' ', ' '), " ").Trim();
            if (line.Length == 0)
            {
                blank++;
                continue;
            }
            if (sb.Length > 0) sb.Append(blank > 0 ? "\n\n" : "\n");
            blank = 0;
            sb.Append(line);
        }
        return sb.ToString();
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)] private static partial Regex CommentRegex();
    [GeneratedRegex(@"<(script|style|head|title)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex InvisibleBlockRegex();
    [GeneratedRegex(@"<at\b[^>]*>(.*?)</at>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex MentionRegex();
    [GeneratedRegex(@"<blockquote\b.*?</blockquote\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex BlockquoteRegex();
    [GeneratedRegex(@"<attachment\b[^>]*>\s*(</attachment>)?", RegexOptions.IgnoreCase)] private static partial Regex AttachmentTagRegex();
    [GeneratedRegex(@"<br\s*/?>|<hr\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex LineBreakRegex();
    [GeneratedRegex(@"</(p|div|tr|li|h[1-6]|table|ul|ol|blockquote|pre)\s*>", RegexOptions.IgnoreCase)] private static partial Regex BlockEndRegex();
    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex ListItemRegex();
    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase)] private static partial Regex CellRegex();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex TagRegex();
    [GeneratedRegex(@"<[a-zA-Z][^>]*>")] private static partial Regex AnyTagRegex();
    [GeneratedRegex(@"[ \t\f\v]+")] private static partial Regex SpacesRegex();
    [GeneratedRegex(@"^-{2,}\s*Original Message\s*-{2,}$", RegexOptions.IgnoreCase)] private static partial Regex OriginalMessageRegex();
    [GeneratedRegex(@"^_{8,}$")] private static partial Regex UnderscoreRegex();
    [GeneratedRegex(@"^\*?(From|De|Von)\*?:\s*\S", RegexOptions.IgnoreCase)] private static partial Regex FromHeaderRegex();
    [GeneratedRegex(@"^\*?(Sent|Date|To|Subject|Cc)\*?:", RegexOptions.IgnoreCase)] private static partial Regex HeaderLineRegex();
    [GeneratedRegex(@"^On\b.{0,250}\bwrote:$", RegexOptions.IgnoreCase)] private static partial Regex WroteRegex();
}
