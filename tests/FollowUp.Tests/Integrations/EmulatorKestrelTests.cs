using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Integrations.GraphEmulator;
using static FollowUp.Tests.Integrations.EmulatorHost;

namespace FollowUp.Tests.Integrations;

/// <summary>The API-process topology: Kestrel hosts the emulator and the Graph services call it over real loopback HTTP.</summary>
public sealed class EmulatorKestrelTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Graph_services_work_over_real_http_with_next_links_on_the_public_port()
    {
        await using var host = await StartSeededAsync(s => s["Microsoft365:PageSize"] = "1", useKestrel: true);

        var email = host.Get<IEmailService>();
        await email.SendAsync(new OutboundEmail("k-1", [new EmailAddress(Ravi.Mail)], [], "First", "One"), Ct);
        await email.SendAsync(new OutboundEmail("k-2", [new EmailAddress(Ravi.Mail)], [], "Second", "Two"), Ct);

        var (messages, cursor, pages) = await DrainAsync(host.Get<IMailSynchronizationService>(),
            new SyncCursor(IntegrationType.OutlookMail, $"{Ravi.Mail}|inbox", null, null, null, null));
        Assert.Equal(["First", "Second"], messages.Select(m => m.Subject));
        Assert.Equal(2, pages);
        Assert.StartsWith(host.Console.BaseAddress!.GetLeftPart(UriPartial.Authority), cursor.DeltaToken);

        var sent = await host.Get<ITeamsService>().SendAsync(new OutboundTeamsMessage("k-3", TeamsTargetKind.User, "Ping", UserId: Ravi.Id), Ct);
        var chatId = GraphEmulatorIds.OneOnOneChat(OfficerId, Ravi.Id);
        var page = await host.Get<ITeamsSynchronizationService>().FetchAsync(new SyncCursor(IntegrationType.TeamsChat, chatId, null, null, null, null), Ct);
        Assert.Equal(sent.ExternalMessageId, Assert.Single(page.Messages).ExternalId);

        var users = await host.GetJsonAsync<List<SimUserDto>>("users");
        Assert.Equal(4, users.Count);
    }
}
