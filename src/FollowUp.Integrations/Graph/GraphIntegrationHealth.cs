using System.Text;
using System.Text.Json;
using FollowUp.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations.Graph;

/// <summary>
/// Connection probe for the admin monitor: acquires the app-only token, calls a cheap endpoint, decodes the granted
/// application roles (and the delegated service account's scopes) from the JWT and lists what is missing from the
/// minimum permission set.
/// </summary>
public sealed class GraphIntegrationHealth : IIntegrationHealth
{
    /// <summary>A required permission and the broader permissions that also satisfy it.</summary>
    internal sealed record Requirement(string Name, params string[] AlsoSatisfiedBy);

    internal static readonly Requirement[] ApplicationRequirements =
    [
        new("Mail.ReadWrite", "Mail.Read"),
        new("Mail.Send"),
        new("Chat.Read.All", "Chat.ReadWrite.All"),
        new("ChannelMessage.Read.All"),
        new("User.Read.All", "User.ReadWrite.All", "Directory.Read.All", "Directory.ReadWrite.All"),
        new("Group.Read.All", "Group.ReadWrite.All", "Directory.Read.All", "Directory.ReadWrite.All"),
        new("Calendars.ReadWrite"),
        new("TeamsActivity.Send"),
    ];

    internal static readonly Requirement[] DelegatedRequirements =
    [
        new("Chat.ReadWrite", "Chat.ReadWrite.All"),
        new("ChannelMessage.Send"),
    ];

    private readonly GraphClient _graph;
    private readonly GraphTokenProvider _tokens;
    private readonly IOptions<GraphOptions> _options;
    private readonly Microsoft365Clock _clock;

    public GraphIntegrationHealth(GraphClient graph, GraphTokenProvider tokens, IOptions<GraphOptions> options, Microsoft365Clock clock)
    {
        _graph = graph;
        _tokens = tokens;
        _options = options;
        _clock = clock;
    }

    public async Task<IntegrationProbe> ProbeAsync(CancellationToken ct)
    {
        var options = _options.Value;
        var mode = options.Mode.ToString();
        var granted = new List<string>();
        var missing = new List<string>();
        var errors = new List<string>();

        string appToken;
        try
        {
            appToken = await _tokens.GetAccessTokenAsync(GraphAuth.Application, ct);
        }
        catch (IntegrationException ex)
        {
            return new IntegrationProbe(mode, false, options.TenantId, options.SenderMailbox, [],
                ApplicationRequirements.Select(r => r.Name).ToList(), ex.Message, _clock.UtcNow);
        }

        var appClaims = TryReadClaims(appToken);
        var roles = appClaims is null ? null : ReadRoles(appClaims.Value);
        if (roles is not null)
        {
            granted.AddRange(roles);
            missing.AddRange(Missing(ApplicationRequirements, roles));
        }
        else
        {
            errors.Add("The app token is not a JWT; granted application permissions could not be inspected.");
        }

        var authenticated = true;
        try
        {
            await _graph.GetPageAsync<GraphUser>("users?$top=1&$select=id", GraphAuth.Application, ct);
        }
        catch (IntegrationException ex)
        {
            authenticated = ex.Kind != Domain.IntegrationErrorKind.Authentication;
            errors.Add(ex.Message);
        }

        try
        {
            var delegatedToken = await _tokens.GetAccessTokenAsync(GraphAuth.ServiceAccount, ct);
            var scopes = TryReadClaims(delegatedToken) is { } claims ? ReadScopes(claims) : null;
            if (scopes is not null)
            {
                granted.AddRange(scopes.Select(s => s + " (delegated)"));
                missing.AddRange(Missing(DelegatedRequirements, scopes).Select(s => s + " (delegated)"));
            }
        }
        catch (IntegrationException ex)
        {
            missing.AddRange(DelegatedRequirements.Select(r => r.Name + " (delegated)"));
            errors.Add($"Teams service account ({options.ServiceAccount}): {ex.Message}");
        }

        return new IntegrationProbe(mode, authenticated, ReadString(appClaims, "tid") ?? options.TenantId, options.SenderMailbox,
            granted.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList(),
            missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            errors.Count == 0 ? null : string.Join(" | ", errors), _clock.UtcNow);
    }

    internal static IEnumerable<string> Missing(IEnumerable<Requirement> requirements, IReadOnlyCollection<string> granted) =>
        requirements
            .Where(r => !granted.Contains(r.Name, StringComparer.OrdinalIgnoreCase)
                        && !r.AlsoSatisfiedBy.Any(a => granted.Contains(a, StringComparer.OrdinalIgnoreCase)))
            .Select(r => r.Name);

    /// <summary>Decodes the payload of a JWT (no signature validation — the token came from our own token request).</summary>
    internal static JsonElement? TryReadClaims(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (FormatException)
        {
            return null; // opaque token
        }
        catch (JsonException)
        {
            return null; // opaque token
        }
    }

    internal static IReadOnlyCollection<string>? ReadRoles(JsonElement claims) =>
        claims.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
            ? roles.EnumerateArray().Select(r => r.GetString()).OfType<string>().ToList()
            : claims.TryGetProperty("scp", out _) ? ReadScopes(claims) : [];

    internal static IReadOnlyCollection<string>? ReadScopes(JsonElement claims) =>
        claims.TryGetProperty("scp", out var scp) && scp.ValueKind == JsonValueKind.String
            ? (scp.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];

    private static string? ReadString(JsonElement? claims, string name) =>
        claims is { } c && c.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
