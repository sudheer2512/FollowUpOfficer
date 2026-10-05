using System.Net;
using System.Text.Json.Nodes;

namespace FollowUp.Integrations.Graph;

/// <summary>A user mentioned in a Teams message: the &lt;at id="n"&gt; index, the Entra user id and the display name.</summary>
internal sealed record TeamsMentionTag(int Id, string UserId, string DisplayName);

/// <summary>Builds Teams HTML message bodies with @mentions the way Graph expects them.</summary>
internal static class TeamsContent
{
    /// <summary>Plain text to Teams HTML (encoded, line breaks preserved).</summary>
    public static string TextToHtml(string text) =>
        WebUtility.HtmlEncode(text.Replace("\r\n", "\n").Trim()).Replace("\n", "<br>");

    /// <summary>
    /// Adds &lt;at id="n"&gt;Name&lt;/at&gt; tags for the mentions: an existing "@Name" in the content is replaced,
    /// otherwise the tag is prepended. Content that already carries &lt;at id=…&gt; tags is left unchanged.
    /// </summary>
    public static (string Html, IReadOnlyList<TeamsMentionTag> Mentions) WithMentions(string html,
        IReadOnlyList<(string UserId, string DisplayName)> mentions)
    {
        var tags = mentions.Select((m, i) => new TeamsMentionTag(i, m.UserId, m.DisplayName)).ToList();
        if (tags.Count == 0 || html.Contains("<at id=", StringComparison.OrdinalIgnoreCase)) return (html, tags);
        var prefix = new List<string>();
        foreach (var tag in tags)
        {
            var at = $"<at id=\"{tag.Id}\">{WebUtility.HtmlEncode(tag.DisplayName)}</at>";
            var token = "@" + WebUtility.HtmlEncode(tag.DisplayName);
            var index = html.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) html = string.Concat(html.AsSpan(0, index), at, html.AsSpan(index + token.Length));
            else prefix.Add(at);
        }
        if (prefix.Count > 0) html = string.Join(" ", prefix) + " " + html;
        return (html, tags);
    }

    /// <summary>Graph "mentions" array for a chatMessage body.</summary>
    public static JsonArray ToJson(IReadOnlyList<TeamsMentionTag> mentions)
    {
        var array = new JsonArray();
        foreach (var m in mentions)
        {
            array.Add(new JsonObject
            {
                ["id"] = m.Id,
                ["mentionText"] = m.DisplayName,
                ["mentioned"] = new JsonObject
                {
                    ["user"] = new JsonObject
                    {
                        ["id"] = m.UserId,
                        ["displayName"] = m.DisplayName,
                        ["userIdentityType"] = "aadUser",
                    },
                },
            });
        }
        return array;
    }
}
