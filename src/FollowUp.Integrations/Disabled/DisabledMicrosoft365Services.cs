using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Integrations.Graph;

namespace FollowUp.Integrations.Disabled;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// Microsoft365:Mode = Disabled. Sends fail with a validation error (Core records it and does not retry),
// synchronisation returns empty final pages, discovery and directory return nothing, health reports Disabled.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

internal static class Disabled
{
    public const string Message = "Microsoft 365 integration disabled";

    public static IntegrationException Error() => new(IntegrationErrorKind.Validation, Message);

    public static SyncPage EmptyPage(SyncCursor cursor) => new([], null, cursor.DeltaToken, cursor.Watermark, true);
}

/// <summary>Email port when Microsoft 365 is disabled.</summary>
public sealed class DisabledEmailService : IEmailService
{
    public Task<DeliveryResult> SendAsync(OutboundEmail email, CancellationToken ct) => throw Disabled.Error();

    public Task<bool?> WasSentAsync(string idempotencyKey, DateTime sinceUtc, CancellationToken ct) => Task.FromResult<bool?>(null);
}

/// <summary>Teams port when Microsoft 365 is disabled.</summary>
public sealed class DisabledTeamsService : ITeamsService
{
    public Task<DeliveryResult> SendAsync(OutboundTeamsMessage message, CancellationToken ct) => throw Disabled.Error();
}

/// <summary>Mail sync port when Microsoft 365 is disabled.</summary>
public sealed class DisabledMailSynchronizationService : IMailSynchronizationService
{
    public Task<IReadOnlyList<SyncSourceInfo>> DiscoverSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SyncSourceInfo>>([]);

    public Task<SyncPage> FetchAsync(SyncCursor cursor, CancellationToken ct) => Task.FromResult(Disabled.EmptyPage(cursor));
}

/// <summary>Teams sync port when Microsoft 365 is disabled.</summary>
public sealed class DisabledTeamsSynchronizationService : ITeamsSynchronizationService
{
    public Task<IReadOnlyList<SyncSourceInfo>> DiscoverSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SyncSourceInfo>>([]);

    public Task<SyncPage> FetchAsync(SyncCursor cursor, CancellationToken ct) => Task.FromResult(Disabled.EmptyPage(cursor));
}

/// <summary>Calendar port when Microsoft 365 is disabled.</summary>
public sealed class DisabledCalendarService : ICalendarService
{
    public Task<string> UpsertEventAsync(CalendarEventRequest request, CancellationToken ct) => throw Disabled.Error();

    public Task DeleteEventAsync(string mailboxAddress, string eventId, CancellationToken ct) => throw Disabled.Error();
}

/// <summary>Directory port when Microsoft 365 is disabled.</summary>
public sealed class DisabledUserDirectoryService : IUserDirectoryService
{
    public Task<IReadOnlyList<DirectoryUser>> ListUsersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DirectoryUser>>([]);

    public Task<IReadOnlyList<DirectoryTeam>> ListTeamsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DirectoryTeam>>([]);
}

/// <summary>Health port when Microsoft 365 is disabled.</summary>
public sealed class DisabledIntegrationHealth(Microsoft365Clock clock) : IIntegrationHealth
{
    public Task<IntegrationProbe> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(new IntegrationProbe(nameof(Microsoft365Mode.Disabled), false, null, null, [], [], Disabled.Message, clock.UtcNow));
}
