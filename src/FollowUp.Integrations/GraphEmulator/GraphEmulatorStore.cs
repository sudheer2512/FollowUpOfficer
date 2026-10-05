using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FollowUp.Core.Abstractions;
using FollowUp.Integrations.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations.GraphEmulator;

/// <summary>Emulator failure with the HTTP status and Graph error code the endpoints return.</summary>
public sealed class GraphEmulatorException(int statusCode, string code, string message) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;

    internal static GraphEmulatorException NotFound(string what) => new(404, "Request_ResourceNotFound", $"Resource '{what}' does not exist.");
    internal static GraphEmulatorException BadRequest(string message) => new(400, "BadRequest", message);
    internal static GraphEmulatorException Forbidden(string message) => new(403, "Forbidden", message);
}

/// <summary>
/// Operations of the emulated tenant, shared by the Graph-shaped endpoints, the simulator console and
/// <see cref="GraphEmulatorTenant"/>. All writes run under the process-wide write lock with a fresh change tracker.
/// </summary>
internal sealed partial class GraphEmulatorStore
{
    /// <summary>Application roles granted to emulator app-only tokens (the documented minimum set).</summary>
    public static readonly string[] AppRoles =
    [
        "Mail.ReadWrite", "Mail.Send", "Chat.Read.All", "ChannelMessage.Read.All", "User.Read.All", "Group.Read.All",
        "Calendars.ReadWrite", "TeamsActivity.Send",
    ];

    /// <summary>Delegated scopes granted to the service account.</summary>
    public static readonly string[] DelegatedScopes = ["Chat.ReadWrite", "Chat.Create", "ChannelMessage.Send", "User.Read", "offline_access"];

    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(90);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly GraphEmulatorDbContext _db;
    private readonly GraphEmulatorState _state;
    private readonly IOptions<GraphOptions> _options;
    private readonly Microsoft365Clock _clock;
    private readonly TimeProvider _time;

    public GraphEmulatorStore(GraphEmulatorDbContext db, GraphEmulatorState state, IOptions<GraphOptions> options, Microsoft365Clock clock,
        TimeProvider time)
    {
        _db = db;
        _state = state;
        _options = options;
        _clock = clock;
        _time = time;
    }

    public GraphEmulatorDbContext Db => _db;
    public GraphOptions Options => _options.Value;

    /// <summary>Business time (follows the application clock).</summary>
    public DateTime Now => _clock.UtcNow;

    /// <summary>Real time (token lifetimes).</summary>
    private DateTime RealNow => _time.GetUtcNow().UtcDateTime;

    // ── Infrastructure ──

    /// <summary>Runs a write under the process-wide lock with a clean change tracker (no stale counters).</summary>
    internal async Task<T> WriteAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _state.WriteLock.WaitAsync(ct);
        try
        {
            _db.ChangeTracker.Clear();
            return await action();
        }
        finally
        {
            _db.ChangeTracker.Clear();
            _state.WriteLock.Release();
        }
    }

    internal Task WriteAsync(Func<Task> action, CancellationToken ct) =>
        WriteAsync(async () =>
        {
            await action();
            return true;
        }, ct);

    /// <summary>Loads (tracked) or creates the settings row. Call inside <see cref="WriteAsync{T}"/>.</summary>
    private async Task<EmulatorSettings> SettingsForUpdateAsync(CancellationToken ct)
    {
        var settings = await _db.Settings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is not null) return settings;
        settings = new EmulatorSettings { Id = 1, UpdatedAt = Now };
        _db.Settings.Add(settings);
        return settings;
    }

    private async Task<EmulatorSettings> SettingsSnapshotAsync(CancellationToken ct) =>
        await _db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct) ?? new EmulatorSettings { Id = 1 };

    /// <summary>Creates the settings row and the system users (sender mailbox, Teams service account).</summary>
    public Task EnsureInitializedAsync(CancellationToken ct) => WriteAsync(async () =>
    {
        await SettingsForUpdateAsync(ct);
        var options = Options;
        await EnsureSystemUserAsync(options.SenderMailbox, "Digital Follow-up Officer", ct);
        if (!string.Equals(options.ServiceAccount, options.SenderMailbox, StringComparison.OrdinalIgnoreCase))
            await EnsureSystemUserAsync(options.ServiceAccount, "Follow-up Officer (Teams)", ct);
        await _db.SaveChangesAsync(ct);
    }, ct);

    private async Task EnsureSystemUserAsync(string mail, string displayName, CancellationToken ct)
    {
        var normalized = GraphEmulatorIds.Normalize(mail);
        if (await _db.Users.AnyAsync(u => u.Mail == normalized || u.UserPrincipalName == normalized, ct)) return;
        if (_db.Users.Local.Any(u => u.Mail == normalized)) return;
        _db.Users.Add(new EmulatorUser
        {
            Id = GraphEmulatorIds.ForUser(normalized),
            DisplayName = displayName,
            Mail = normalized,
            UserPrincipalName = normalized,
            JobTitle = "Follow-up Officer",
            Department = "Office of the CEO",
            CreatedAt = Now,
        });
    }

    // ── Directory ──

    /// <summary>Finds a user by id, mail or UPN (case-insensitive for addresses).</summary>
    public async Task<EmulatorUser?> FindUserAsync(string idOrAddress, CancellationToken ct)
    {
        var key = idOrAddress.Trim();
        if (key.Length == 0) return null;
        var normalized = key.ToLowerInvariant();
        return await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == key || u.Mail == normalized || u.UserPrincipalName == normalized, ct);
    }

    public async Task<EmulatorUser> RequireUserAsync(string idOrAddress, CancellationToken ct) =>
        await FindUserAsync(idOrAddress, ct) ?? throw new GraphEmulatorException(404, "Request_ResourceNotFound",
            $"Resource '{idOrAddress}' does not exist or one of its queried reference-property objects are not present.");

    public Task<List<EmulatorUser>> ListUsersAsync(CancellationToken ct) =>
        _db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.Id).ToListAsync(ct);

    public Task<Dictionary<string, EmulatorUser>> UserMapAsync(CancellationToken ct) =>
        _db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, StringComparer.Ordinal, ct);

    /// <summary>Idempotent upsert of users, teams (channels, members) and chats (members).</summary>
    public Task EnsureDirectoryAsync(IReadOnlyList<TenantUser> users, IReadOnlyList<TenantTeam> teams, IReadOnlyList<TenantChat> chats,
        CancellationToken ct) => WriteAsync(async () =>
    {
        var now = Now;
        foreach (var u in users)
        {
            if (string.IsNullOrWhiteSpace(u.Id) || string.IsNullOrWhiteSpace(u.Mail))
                throw GraphEmulatorException.BadRequest("Every tenant user needs an id and a mail address.");
            var mail = GraphEmulatorIds.Normalize(u.Mail);
            var entity = await _db.Users.FindAsync([u.Id], ct);
            if (entity is null)
            {
                var clash = await _db.Users.FirstOrDefaultAsync(x => x.Mail == mail && x.Id != u.Id, ct);
                if (clash is not null)
                    throw new GraphEmulatorException(409, "Conflict", $"Mail {mail} already belongs to user {clash.Id}.");
                entity = new EmulatorUser { Id = u.Id, CreatedAt = now };
                _db.Users.Add(entity);
            }
            entity.DisplayName = u.DisplayName;
            entity.Mail = mail;
            entity.UserPrincipalName = mail;
            entity.JobTitle = u.JobTitle;
            entity.Department = u.Department;
            entity.ManagerId = u.ManagerId;
            entity.AccountEnabled = true;
        }
        await _db.SaveChangesAsync(ct);

        foreach (var t in teams)
        {
            var team = await _db.Teams.FindAsync([t.Id], ct);
            if (team is null)
            {
                team = new EmulatorTeam { Id = t.Id, CreatedAt = now };
                _db.Teams.Add(team);
            }
            team.DisplayName = t.DisplayName;
            foreach (var c in t.Channels)
            {
                var channel = await _db.Channels.FindAsync([t.Id, c.Id], ct);
                if (channel is null)
                {
                    channel = new EmulatorChannel { TeamId = t.Id, Id = c.Id, CreatedAt = now };
                    _db.Channels.Add(channel);
                }
                channel.DisplayName = c.DisplayName;
            }
            var members = await _db.TeamMembers.Where(m => m.TeamId == t.Id).ToListAsync(ct);
            _db.TeamMembers.RemoveRange(members.Where(m => !t.MemberIds.Contains(m.UserId)));
            foreach (var userId in t.MemberIds.Distinct().Where(id => members.All(m => m.UserId != id)))
                _db.TeamMembers.Add(new EmulatorTeamMember { TeamId = t.Id, UserId = userId });
        }
        await _db.SaveChangesAsync(ct);

        foreach (var c in chats)
        {
            var memberIds = c.MemberIds.Distinct().ToList();
            var chatType = NormalizeChatType(c.ChatType);
            string? oneOnOneKey = null;
            if (chatType == "oneOnOne")
            {
                if (memberIds.Count != 2) throw GraphEmulatorException.BadRequest($"1:1 chat {c.Id} needs exactly two members.");
                oneOnOneKey = GraphEmulatorIds.OneOnOneKey(memberIds[0], memberIds[1]);
                var existing = await _db.Chats.FirstOrDefaultAsync(x => x.OneOnOneKey == oneOnOneKey && x.Id != c.Id, ct);
                if (existing is not null)
                    throw new GraphEmulatorException(409, "Conflict", $"A 1:1 chat between these users already exists ({existing.Id}).");
            }
            var chat = await _db.Chats.FindAsync([c.Id], ct);
            if (chat is null)
            {
                chat = new EmulatorChat { Id = c.Id, CreatedAt = now, LastUpdatedAt = now };
                _db.Chats.Add(chat);
            }
            chat.ChatType = chatType;
            chat.Topic = c.Topic;
            chat.OneOnOneKey = oneOnOneKey;
            var members = await _db.ChatMembers.Where(m => m.ChatId == c.Id).ToListAsync(ct);
            _db.ChatMembers.RemoveRange(members.Where(m => !memberIds.Contains(m.UserId)));
            foreach (var userId in memberIds.Where(id => members.All(m => m.UserId != id)))
                _db.ChatMembers.Add(new EmulatorChatMember { ChatId = c.Id, UserId = userId, JoinedAt = now });
        }
        await _db.SaveChangesAsync(ct);
    }, ct);

    private static string NormalizeChatType(string? chatType) => chatType?.Trim().ToLowerInvariant() switch
    {
        "oneonone" or "one-on-one" or "1:1" => "oneOnOne",
        "meeting" => "meeting",
        _ => "group",
    };

    public Task<List<EmulatorTeam>> ListTeamsAsync(CancellationToken ct) =>
        _db.Teams.AsNoTracking().OrderBy(t => t.DisplayName).ToListAsync(ct);

    public async Task<EmulatorTeam> RequireTeamAsync(string teamId, CancellationToken ct) =>
        await _db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == teamId, ct) ?? throw GraphEmulatorException.NotFound(teamId);

    public Task<List<EmulatorChannel>> ListChannelsAsync(string? teamId, CancellationToken ct) =>
        _db.Channels.AsNoTracking().Where(c => teamId == null || c.TeamId == teamId).OrderBy(c => c.TeamId).ThenBy(c => c.DisplayName).ToListAsync(ct);

    public async Task<EmulatorChannel> RequireChannelAsync(string teamId, string channelId, CancellationToken ct) =>
        await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.TeamId == teamId && c.Id == channelId, ct)
        ?? throw GraphEmulatorException.NotFound($"{teamId}/channels/{channelId}");

    // ── Faults and delta generation ──

    public async Task<TenantFaults> GetFaultsAsync(CancellationToken ct)
    {
        var cached = _state.CachedFaults(RealNow);
        if (cached is not null) return cached;
        var s = await SettingsSnapshotAsync(ct);
        var faults = new TenantFaults(s.Outage, s.ThrottlePercent, s.AuthFailure, s.LatencyMs);
        _state.CacheFaults(faults, RealNow);
        return faults;
    }

    public async Task<int> GetDeltaEpochAsync(CancellationToken ct) => (await SettingsSnapshotAsync(ct)).DeltaEpoch;

    public Task<TenantFaults> SetFaultsAsync(TenantFaults faults, CancellationToken ct) => WriteAsync(async () =>
    {
        var s = await SettingsForUpdateAsync(ct);
        s.Outage = faults.Outage;
        s.ThrottlePercent = Math.Clamp(faults.ThrottlePercent, 0, 100);
        s.AuthFailure = faults.AuthFailure;
        s.LatencyMs = Math.Clamp(faults.LatencyMs, 0, 60_000);
        s.UpdatedAt = Now;
        await _db.SaveChangesAsync(ct);
        var applied = new TenantFaults(s.Outage, s.ThrottlePercent, s.AuthFailure, s.LatencyMs);
        _state.CacheFaults(applied, RealNow);
        return applied;
    }, ct);

    /// <summary>Invalidates every outstanding delta / skip token (the next delta call returns 410 syncStateNotFound).</summary>
    public Task<int> InvalidateDeltaTokensAsync(CancellationToken ct) => WriteAsync(async () =>
    {
        var s = await SettingsForUpdateAsync(ct);
        s.DeltaEpoch++;
        s.UpdatedAt = Now;
        await _db.SaveChangesAsync(ct);
        return s.DeltaEpoch;
    }, ct);

    /// <summary>Allocates the next change sequence. Call inside <see cref="WriteAsync{T}"/>; saved with the caller's changes.</summary>
    private async Task<long> NextSequenceAsync(CancellationToken ct)
    {
        var s = await SettingsForUpdateAsync(ct);
        return ++s.ChangeSequence;
    }

    // ── Tokens ──

    internal sealed record IssuedTokenSet(string AccessToken, int ExpiresIn, string? RefreshToken, string Scope);

    /// <summary>Issues a JWT-shaped access token (roles for app tokens, scp for delegated) and records its hash.</summary>
    public Task<IssuedTokenSet> IssueTokenAsync(string clientId, EmulatorUser? delegatedUser, CancellationToken ct) => WriteAsync(async () =>
    {
        var now = RealNow;
        var options = Options;
        var delegated = delegatedUser is not null;
        var claims = new Dictionary<string, object?>
        {
            ["aud"] = "https://graph.microsoft.com",
            ["iss"] = $"https://sts.windows.net/{options.TenantId}/",
            ["iat"] = new DateTimeOffset(now).ToUnixTimeSeconds(),
            ["nbf"] = new DateTimeOffset(now).ToUnixTimeSeconds(),
            ["exp"] = new DateTimeOffset(now + AccessTokenLifetime).ToUnixTimeSeconds(),
            ["tid"] = options.TenantId,
            ["appid"] = clientId,
            ["app_displayname"] = "FollowUp Officer (emulator)",
            ["idtyp"] = delegated ? "user" : "app",
            ["ver"] = "1.0",
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        if (delegated)
        {
            claims["scp"] = string.Join(' ', DelegatedScopes);
            claims["oid"] = delegatedUser!.Id;
            claims["upn"] = delegatedUser.UserPrincipalName;
            claims["name"] = delegatedUser.DisplayName;
        }
        else
        {
            claims["roles"] = AppRoles;
            claims["oid"] = GraphEmulatorIds.ForUser("app:" + clientId);
        }

        var accessToken = CreateJwt(claims, options.ClientSecret ?? "");
        var kind = delegated ? "delegated" : "app";
        _db.IssuedTokens.Add(new EmulatorIssuedToken
        {
            TokenHash = Hash(accessToken), Kind = kind, ClientId = clientId, UserId = delegatedUser?.Id, IssuedAt = now,
            ExpiresAt = now + AccessTokenLifetime,
        });
        string? refreshToken = null;
        if (delegated)
        {
            refreshToken = "0.AemuRT." + GraphEmulatorIds.RandomBase64Url(48);
            _db.IssuedTokens.Add(new EmulatorIssuedToken
            {
                TokenHash = Hash(refreshToken), Kind = "refresh", ClientId = clientId, UserId = delegatedUser!.Id, IssuedAt = now,
                ExpiresAt = now + RefreshTokenLifetime,
            });
        }
        await _db.IssuedTokens.Where(t => t.ExpiresAt < now).ExecuteDeleteAsync(ct);
        await _db.SaveChangesAsync(ct);
        _state.Tokens[accessToken] = new EmulatorPrincipal(kind, clientId, delegatedUser?.Id, now + AccessTokenLifetime);
        var scope = delegated ? string.Join(' ', DelegatedScopes) : GraphOptions.GraphScope;
        return new IssuedTokenSet(accessToken, (int)AccessTokenLifetime.TotalSeconds - 1, refreshToken, scope);
    }, ct);

    /// <summary>Validates a bearer token issued by the emulator.</summary>
    public async Task<EmulatorPrincipal?> ValidateAccessTokenAsync(string token, CancellationToken ct)
    {
        var now = RealNow;
        if (_state.Tokens.TryGetValue(token, out var cached))
        {
            if (cached.ExpiresAt > now) return cached;
            _state.Tokens.TryRemove(token, out _);
            return null;
        }
        var hash = Hash(token);
        var row = await _db.IssuedTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null || row.Kind == "refresh" || row.ExpiresAt <= now) return null;
        var principal = new EmulatorPrincipal(row.Kind, row.ClientId, row.UserId, row.ExpiresAt);
        _state.Tokens[token] = principal;
        return principal;
    }

    /// <summary>
    /// Resolves the user of a refresh token: the configured service-account token maps to the service account, tokens
    /// issued by the emulator map to the user they were issued for. Null when the token is unknown or expired.
    /// </summary>
    public async Task<EmulatorUser?> ResolveRefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;
        if (!string.IsNullOrEmpty(Options.ServiceAccountRefreshToken) && refreshToken == Options.ServiceAccountRefreshToken)
        {
            var account = await FindUserAsync(Options.ServiceAccount, ct);
            if (account is not null) return account;
            await EnsureInitializedAsync(ct);
            return await FindUserAsync(Options.ServiceAccount, ct);
        }
        var hash = Hash(refreshToken);
        var now = RealNow;
        var row = await _db.IssuedTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == "refresh" && t.ExpiresAt > now, ct);
        return row?.UserId is { } userId ? await FindUserAsync(userId, ct) : null;
    }

    private static string CreateJwt(Dictionary<string, object?> claims, string secret)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = Base64Url(HMACSHA256.HashData(SHA256.HashData(Encoding.UTF8.GetBytes("emulator:" + secret)),
            Encoding.ASCII.GetBytes(header + "." + payload)));
        return $"{header}.{payload}.{signature}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ── JSON column helpers ──

    internal static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Json);

    internal static List<T> FromJson<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];
}
