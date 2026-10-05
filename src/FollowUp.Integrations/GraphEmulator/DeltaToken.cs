using System.Text;
using System.Text.Json;

namespace FollowUp.Integrations.GraphEmulator;

/// <summary>
/// Opaque state carried in emulator $deltatoken / $skiptoken values (base64url JSON). A delta token resumes after
/// <see cref="FromSeq"/>; a skip token continues a round after <see cref="AfterSeq"/> up to the round's <see cref="UpToSeq"/> snapshot.
/// </summary>
internal sealed record DeltaToken(
    string Resource,
    int Epoch,
    long FromSeq,
    long UpToSeq,
    long AfterSeq,
    DateTime? Since,
    bool Initial,
    DateTime IssuedAt)
{
    /// <summary>Tokens older than this are rejected with 410 syncStateNotFound.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Encode() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes a token; null when it is not one of ours.</summary>
    public static DeltaToken? TryDecode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<DeltaToken>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)), Json);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
