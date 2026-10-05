using FollowUp.Core.Abstractions;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Core.Services;
using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Tests.Core.Queries;

public sealed class IntegrationJobTests
{
    private sealed class RecordingCalendar : ICalendarService
    {
        public List<CalendarEventRequest> Upserts { get; } = [];
        public List<(string Mailbox, string EventId)> Deletes { get; } = [];

        public Task<string> UpsertEventAsync(CalendarEventRequest request, CancellationToken ct)
        {
            Upserts.Add(request);
            return Task.FromResult(request.ExistingEventId ?? $"evt-{Upserts.Count}");
        }

        public Task DeleteEventAsync(string mailboxAddress, string eventId, CancellationToken ct)
        {
            Deletes.Add((mailboxAddress, eventId));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Calendar_sync_pushes_important_deadlines_once_updates_on_change_and_deletes_when_closed()
    {
        var calendar = new RecordingCalendar();
        await using var host = await CoreTestHost.CreateAsync(s => s.AddSingleton<ICalendarService>(calendar));
        var org = host.Org;
        var important = await host.CreateTaskAsync(new CreateTaskRequest
        {
            Title = "Renew the ISO certificate", PrimaryAssigneeId = org.DevMember, DueDate = TestOrg.Today.AddDays(3), Criticality = Criticality.Important,
        });
        await host.CreateTaskAsync(new CreateTaskRequest { Title = "Routine filing", PrimaryAssigneeId = org.DevMember, DueDate = TestOrg.Today.AddDays(3) });

        async Task<int> SyncAsync()
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<CalendarSyncService>().SyncAsync(CancellationToken.None);
        }

        Assert.Equal(2, await SyncAsync());                                   // assignee + executive delegator; the normal task is ignored
        Assert.Equal(["ceo@wings.example", "dev1@wings.example"], calendar.Upserts.Select(u => u.MailboxAddress).Order().ToList());
        Assert.All(calendar.Upserts, u => Assert.Null(u.Start));               // all-day
        Assert.Equal(0, await SyncAsync());                                   // unchanged fingerprint → no Graph call
        Assert.Equal(2, calendar.Upserts.Count);

        await using (var ceo = await host.AsAsync(org.Ceo))
            await ceo.ServiceProvider.GetRequiredService<TaskService>().UpdateAsync(important.Id, new UpdateTaskRequest { DueDate = TestOrg.Today.AddDays(6) }, CancellationToken.None);
        Assert.Equal(2, await SyncAsync());
        Assert.All(calendar.Upserts.Skip(2), u => Assert.NotNull(u.ExistingEventId));
        Assert.All(calendar.Upserts.Skip(2), u => Assert.Equal(TestOrg.Today.AddDays(6), u.Date));

        await using (var ceo = await host.AsAsync(org.Ceo))
            await ceo.ServiceProvider.GetRequiredService<TaskService>().CompleteAsync(important.Id, new CompleteRequest(null, "Renewed", null), CancellationToken.None);
        Assert.Equal(2, await SyncAsync());
        Assert.Equal(2, calendar.Deletes.Count);
        await using var check = host.Services.CreateAsyncScope();
        Assert.All(await check.ServiceProvider.GetRequiredService<FollowUpDbContext>().CalendarEntries.ToListAsync(), e => Assert.True(e.Deleted));
    }

    private sealed class FakeDirectory : IUserDirectoryService
    {
        public Task<IReadOnlyList<DirectoryUser>> ListUsersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DirectoryUser>>(
        [
            new DirectoryUser("aad-raj", "Raj Patel", "FIN1@wings.example", "raj@wings.onmicrosoft.com", "Senior Accountant", "Finance", true, null),
            new DirectoryUser("aad-stranger", "Someone Else", "someone@elsewhere.example", null, "CFO", null, true, null),
        ]);

        public Task<IReadOnlyList<DirectoryTeam>> ListTeamsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DirectoryTeam>>(
            [new DirectoryTeam("team-fin", "Finance Team", [new DirectoryChannel("ch-2", "Month end"), new DirectoryChannel("ch-1", "General")])]);
    }

    [Fact]
    public async Task Directory_sync_matches_by_email_and_never_creates_or_overwrites()
    {
        await using var host = await CoreTestHost.CreateAsync(s => s.AddSingleton<IUserDirectoryService, FakeDirectory>());
        int changed;
        await using (var scope = host.Services.CreateAsyncScope())
            changed = await scope.ServiceProvider.GetRequiredService<OrgService>().SyncDirectoryAsync(CancellationToken.None);
        Assert.Equal(2, changed);

        await using var check = host.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<FollowUpDbContext>();
        var raj = await db.Employees.SingleAsync(e => e.Id == host.Org.FinanceMember);
        Assert.Equal("aad-raj", raj.ExternalUserId);
        Assert.Equal("Accountant", raj.JobTitle);                              // existing titles are kept
        Assert.Equal(7, await db.Employees.CountAsync());                      // nobody created
        var team = await db.Teams.SingleAsync(t => t.Id == host.Org.FinanceTeam);
        Assert.Equal("team-fin", team.TeamsTeamId);
        Assert.Equal("ch-1", team.TeamsChannelId);
    }
}
