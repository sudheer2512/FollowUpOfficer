using System.Globalization;
using System.Security.Cryptography;
using FollowUp.Domain;

namespace FollowUp.Integrations.GraphEmulator;

/// <summary>
/// Graph-like identifiers used by the emulator. Users seeded through IDemoTenant bring their own ids; <see cref="ForUser"/>
/// derives the conventional deterministic id from a mail address so seeders and the emulator agree.
/// </summary>
public static class GraphEmulatorIds
{
    /// <summary>Deterministic Entra-style object id (GUID string) for a mail address.</summary>
    public static string ForUser(string mail) => Ids.Deterministic("m365-user", Normalize(mail)).ToString();

    /// <summary>Deterministic team (group) id for a team name.</summary>
    public static string ForTeam(string name) => Ids.Deterministic("m365-team", name.Trim().ToLowerInvariant()).ToString();

    /// <summary>Deterministic channel id in the Teams format "19:{hex}@thread.tacv2".</summary>
    public static string ForChannel(string teamId, string channelName) =>
        $"19:{Ids.Deterministic("m365-channel", teamId + "|" + channelName.Trim().ToLowerInvariant()):N}@thread.tacv2";

    /// <summary>Teams 1:1 chat id: "19:{userA}_{userB}@unq.gbl.spaces" (ordinal order, as Teams forms it).</summary>
    public static string OneOnOneChat(string userId1, string userId2)
    {
        var (a, b) = Order(userId1, userId2);
        return $"19:{a}_{b}@unq.gbl.spaces";
    }

    /// <summary>Key that makes a 1:1 chat unique per user pair.</summary>
    public static string OneOnOneKey(string userId1, string userId2)
    {
        var (a, b) = Order(userId1, userId2);
        return $"{a}|{b}";
    }

    /// <summary>Group chat id "19:{hex}@thread.v2".</summary>
    public static string NewGroupChat() => $"19:{Guid.NewGuid():N}@thread.v2";

    /// <summary>Outlook item id ("AAMk…", URL-safe base64).</summary>
    public static string NewMailItemId() => "AAMk" + RandomBase64Url(36);

    /// <summary>Outlook conversation id ("AAQk…").</summary>
    public static string NewConversationId() => "AAQk" + RandomBase64Url(24);

    /// <summary>Calendar event id ("AAMk…").</summary>
    public static string NewEventId() => "AAMkAGE" + RandomBase64Url(33);

    /// <summary>RFC 5322 Message-ID.</summary>
    public static string NewInternetMessageId() => $"<{Guid.NewGuid():N}@followup.emulator>";

    /// <summary>Teams message id: Unix epoch milliseconds as a string, like real Graph.</summary>
    public static string TeamsMessageId(DateTime createdUtc) =>
        new DateTimeOffset(DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    internal static string Normalize(string mail) => mail.Trim().ToLowerInvariant();

    internal static string RandomBase64Url(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string A, string B) Order(string x, string y) => string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);
}
