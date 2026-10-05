using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Integrations.GraphEmulator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FollowUp.Tests.Integrations.EmulatorHost;

namespace FollowUp.Tests.Integrations;

public sealed class EmulatorResilienceTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private EmulatorHost _host = null!;

    public async Task InitializeAsync() => _host = await StartSeededAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Full_throttling_surfaces_as_throttled_and_recovers_when_lifted()
    {
        var directory = _host.Get<IUserDirectoryService>();
        await _host.SetFaultsAsync(new TenantFaults(false, 100, false, 0));

        var ex = await Assert.ThrowsAsync<IntegrationException>(() => directory.ListUsersAsync(Ct));
        Assert.Equal(IntegrationErrorKind.Throttled, ex.Kind);
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), ex.RetryAfter);

        await _host.SetFaultsAsync(new TenantFaults(false, 0, false, 0));
        var users = await directory.ListUsersAsync(Ct);
        Assert.Contains(users, u => u.Mail == Ceo.Mail && u.ManagerExternalId is null);
        Assert.Contains(users, u => u.Mail == Ravi.Mail && u.ManagerExternalId == Ceo.Id && u.Department == "Finance");
    }

    [Fact]
    public async Task Outage_is_transient()
    {
        await _host.SetFaultsAsync(new TenantFaults(true, 0, false, 0));

        var ex = await Assert.ThrowsAsync<IntegrationException>(() =>
            _host.Get<IMailSynchronizationService>().FetchAsync(new SyncCursor(IntegrationType.OutlookMail, $"{Ceo.Mail}|inbox", null, null, null, null), Ct));

        Assert.Equal(IntegrationErrorKind.Transient, ex.Kind);
        Assert.Equal(503, ex.StatusCode);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Authentication_failure_is_reported_and_clears()
    {
        var directory = _host.Get<IUserDirectoryService>();
        await directory.ListUsersAsync(Ct); // token cached

        await _host.SetFaultsAsync(new TenantFaults(false, 0, true, 0));
        var ex = await Assert.ThrowsAsync<IntegrationException>(() => directory.ListUsersAsync(Ct));
        Assert.Equal(IntegrationErrorKind.Authentication, ex.Kind);
        var probe = await _host.Get<IIntegrationHealth>().ProbeAsync(Ct);
        Assert.False(probe.Authenticated);
        Assert.NotNull(probe.Error);

        await _host.SetFaultsAsync(new TenantFaults(false, 0, false, 0));
        Assert.NotEmpty(await directory.ListUsersAsync(Ct));
    }

    [Fact]
    public async Task Health_probe_reports_granted_permissions()
    {
        var probe = await _host.Get<IIntegrationHealth>().ProbeAsync(Ct);

        Assert.Equal("Emulator", probe.Mode);
        Assert.True(probe.Authenticated);
        Assert.Null(probe.Error);
        Assert.Empty(probe.MissingPermissions);
        Assert.Equal("contoso-emulator", probe.TenantId);
        Assert.Equal("followup@contoso.com", probe.Account);
        Assert.Contains("Mail.Send", probe.GrantedPermissions);
        Assert.Contains("TeamsActivity.Send", probe.GrantedPermissions);
        Assert.Contains("ChannelMessage.Send (delegated)", probe.GrantedPermissions);
    }

    [Fact]
    public async Task Directory_lists_teams_with_channels()
    {
        var teams = await _host.Get<IUserDirectoryService>().ListTeamsAsync(Ct);

        var team = Assert.Single(teams);
        Assert.Equal(TeamId, team.TeamId);
        Assert.Equal("Operations", team.DisplayName);
        Assert.Equal(ChannelId, Assert.Single(team.Channels).ChannelId);
    }

    [Fact]
    public async Task Calendar_upsert_is_idempotent_by_transaction_id()
    {
        var calendar = _host.Get<ICalendarService>();
        var request = new CalendarEventRequest("task-5:deadline", Ceo.Mail, null, "FT-5 due", "Deadline for FT-5", new DateOnly(2026, 10, 20),
            new TimeOnly(10, 0), new TimeOnly(10, 30), "India Standard Time", true, 30);

        var id = await calendar.UpsertEventAsync(request, Ct);
        var retried = await calendar.UpsertEventAsync(request, Ct); // a retried create: same transactionId
        Assert.Equal(id, retried);
        var created = Assert.Single(await EventsAsync());
        Assert.Equal("2026-10-20T10:00:00", created.StartDateTime);
        Assert.Equal("2026-10-20T10:30:00", created.EndDateTime);
        Assert.Equal("India Standard Time", created.StartTimeZone);
        Assert.Equal("high", created.Importance);
        Assert.Equal(30, created.ReminderMinutesBeforeStart);

        var updated = await calendar.UpsertEventAsync(request with
        {
            ExistingEventId = id, Subject = "FT-5 due (moved)", Date = new DateOnly(2026, 10, 22), Start = null, End = null,
        }, Ct);
        Assert.Equal(id, updated);
        var moved = Assert.Single(await EventsAsync());
        Assert.Equal("FT-5 due (moved)", moved.Subject);
        Assert.True(moved.IsAllDay);
        Assert.Equal("2026-10-22T00:00:00", moved.StartDateTime);
        Assert.Equal("2026-10-23T00:00:00", moved.EndDateTime);

        await calendar.DeleteEventAsync(Ceo.Mail, id, Ct);
        Assert.Empty(await EventsAsync());
        await calendar.DeleteEventAsync(Ceo.Mail, id, Ct); // already gone: idempotent

        var recreated = await calendar.UpsertEventAsync(request with { ExistingEventId = id, Key = "task-5:deadline:v2" }, Ct);
        Assert.NotEqual(id, recreated);
        Assert.Single(await EventsAsync());
    }

    private async Task<List<EmulatorEvent>> EventsAsync()
    {
        await using var scope = _host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GraphEmulatorDbContext>();
        return await db.Events.AsNoTracking().Where(e => e.Mailbox == Ceo.Mail).ToListAsync();
    }
}
