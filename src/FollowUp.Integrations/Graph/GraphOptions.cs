namespace FollowUp.Integrations.Graph;

/// <summary>How the Microsoft 365 integration talks to a tenant.</summary>
public enum Microsoft365Mode
{
    /// <summary>The Graph services call the in-process Graph emulator ("/graph-emulator").</summary>
    Emulator,
    /// <summary>The Graph services call Microsoft Graph and Microsoft Entra ID.</summary>
    Graph,
    /// <summary>No Microsoft 365 integration: sends fail with a validation error, syncs return empty pages.</summary>
    Disabled,
}

/// <summary>
/// Microsoft 365 connection settings, bound from configuration section "Microsoft365".
/// In Emulator mode the authority and Graph base URL default to "{PublicBaseUrl}/graph-emulator".
/// </summary>
public sealed class GraphOptions
{
    public const string SectionName = "Microsoft365";
    public const string DefaultAuthorityHost = "https://login.microsoftonline.com";
    public const string DefaultGraphBaseUrl = "https://graph.microsoft.com/v1.0";
    public const string GraphScope = "https://graph.microsoft.com/.default";
    public const string EmulatorPathPrefix = "/graph-emulator";

    public Microsoft365Mode Mode { get; set; } = Microsoft365Mode.Emulator;

    /// <summary>Entra tenant id (GUID or verified domain).</summary>
    public string? TenantId { get; set; }

    /// <summary>App registration (client) id.</summary>
    public string? ClientId { get; set; }

    /// <summary>App registration client secret (client-credentials flow).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Token authority host. Default https://login.microsoftonline.com (emulator: "{PublicBaseUrl}/graph-emulator").</summary>
    public string? AuthorityHost { get; set; }

    /// <summary>Graph base URL including version. Default https://graph.microsoft.com/v1.0 (emulator: "{PublicBaseUrl}/graph-emulator/v1.0").</summary>
    public string? GraphBaseUrl { get; set; }

    /// <summary>Public base URL of the API process hosting the emulator (Emulator mode only).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5860";

    /// <summary>Mailbox the "Digital Follow-up Officer" sends reminders from.</summary>
    public string SenderMailbox { get; set; } = "followup@contoso.com";

    /// <summary>Delegated service account used to post Teams chat / channel messages (Graph allows posting only with delegated permissions).</summary>
    public string? ServiceAccountUpn { get; set; }

    /// <summary>Refresh token of the delegated service account (rotated tokens are kept by <see cref="IGraphCredentialSource"/>).</summary>
    public string? ServiceAccountRefreshToken { get; set; }

    /// <summary>Preferred page size for delta and list queries.</summary>
    public int PageSize { get; set; } = 50;

    /// <summary>Retries for 429 / 503 / 504 / timeouts / network failures.</summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>Per-attempt HTTP timeout.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Base delay of the exponential backoff used when the server sends no Retry-After.</summary>
    public double RetryBaseDelaySeconds { get; set; } = 1;

    /// <summary>Upper bound for any single retry wait, including a server-sent Retry-After.</summary>
    public double MaxRetryDelaySeconds { get; set; } = 60;

    /// <summary>History window of a first synchronisation that has no watermark.</summary>
    public int InitialSyncDays { get; set; } = 14;

    /// <summary>Upper bound of chat messages fetched by one chat FetchAsync call before it continues on the next call.</summary>
    public int MaxChatMessagesPerFetch { get; set; } = 500;

    /// <summary>Upper bound of replies fetched per channel thread root.</summary>
    public int MaxRepliesPerThread { get; set; } = 1000;

    /// <summary>Mailboxes whose inbox and sent items are synchronised. Default: [SenderMailbox].</summary>
    public string[] MonitoredMailboxes { get; set; } = [];

    /// <summary>Channels to synchronise as "teamId|channelId". Empty: every channel of every team.</summary>
    public string[] MonitoredChannels { get; set; } = [];

    /// <summary>Teams deep link used as the topic of activity-feed notifications.</summary>
    public string ActivityFeedWebUrl { get; set; } = "https://teams.microsoft.com";

    /// <summary>Activity feed topic text.</summary>
    public string ActivityFeedTopic { get; set; } = "Digital Follow-up Officer";

    /// <summary>After sendMail, look the message up in Sent Items to return its Internet-Message-Id.</summary>
    public bool ResolveSentMessageId { get; set; } = true;

    /// <summary>Expand the manager of every user when listing the directory.</summary>
    public bool ExpandManager { get; set; } = true;

    /// <summary>Token authority host after mode defaults.</summary>
    public string ResolvedAuthorityHost => (string.IsNullOrWhiteSpace(AuthorityHost)
        ? Mode == Microsoft365Mode.Emulator ? PublicBaseUrl.TrimEnd('/') + EmulatorPathPrefix : DefaultAuthorityHost
        : AuthorityHost).TrimEnd('/');

    /// <summary>Graph base URL after mode defaults, without a trailing slash.</summary>
    public string ResolvedGraphBaseUrl => (string.IsNullOrWhiteSpace(GraphBaseUrl)
        ? Mode == Microsoft365Mode.Emulator ? PublicBaseUrl.TrimEnd('/') + EmulatorPathPrefix + "/v1.0" : DefaultGraphBaseUrl
        : GraphBaseUrl).TrimEnd('/');

    /// <summary>OAuth 2.0 v2 token endpoint of the tenant.</summary>
    public string TokenEndpoint => $"{ResolvedAuthorityHost}/{Uri.EscapeDataString(TenantId ?? "common")}/oauth2/v2.0/token";

    /// <summary>The delegated service account (defaults to the sender mailbox).</summary>
    public string ServiceAccount => string.IsNullOrWhiteSpace(ServiceAccountUpn) ? SenderMailbox : ServiceAccountUpn;

    /// <summary>Monitored mailboxes after defaults.</summary>
    public IReadOnlyList<string> ResolvedMonitoredMailboxes => MonitoredMailboxes.Length > 0
        ? MonitoredMailboxes.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        : [SenderMailbox];

    /// <summary>Fills the well-known emulator credentials so a demo needs no configuration.</summary>
    public void ApplyDefaults()
    {
        if (PageSize <= 0) PageSize = 50;
        if (MaxRetries < 0) MaxRetries = 0;
        if (RequestTimeoutSeconds <= 0) RequestTimeoutSeconds = 60;
        if (InitialSyncDays <= 0) InitialSyncDays = 14;
        if (MaxChatMessagesPerFetch <= 0) MaxChatMessagesPerFetch = 500;
        if (MaxRepliesPerThread <= 0) MaxRepliesPerThread = 1000;
        if (Mode != Microsoft365Mode.Emulator) return;
        if (string.IsNullOrWhiteSpace(TenantId)) TenantId = EmulatorDefaults.TenantId;
        if (string.IsNullOrWhiteSpace(ClientId)) ClientId = EmulatorDefaults.ClientId;
        if (string.IsNullOrWhiteSpace(ClientSecret)) ClientSecret = EmulatorDefaults.ClientSecret;
        if (string.IsNullOrWhiteSpace(ServiceAccountRefreshToken)) ServiceAccountRefreshToken = EmulatorDefaults.RefreshToken;
    }
}

/// <summary>Credentials the emulator accepts when none are configured.</summary>
public static class EmulatorDefaults
{
    public const string TenantId = "contoso-emulator";
    public const string ClientId = "followup-officer-emulator";
    public const string ClientSecret = "emulator-client-secret";
    public const string RefreshToken = "emulator-service-account-refresh-token";
}
