using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Integrations;
using FollowUp.Integrations.Graph;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FollowUp.Tests.Integrations;

public sealed class DisabledModeTests
{
    private static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Microsoft365:Mode"] = "Disabled" })
            .Build();
        return new ServiceCollection()
            .AddMicrosoft365Integration(configuration, "")
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task Sends_fail_with_a_validation_error_and_syncs_are_empty()
    {
        await using var sp = Build();
        var ct = CancellationToken.None;

        var mail = await Assert.ThrowsAsync<IntegrationException>(() => sp.GetRequiredService<IEmailService>().SendAsync(
            new OutboundEmail("k", [new EmailAddress("a@b.c")], [], "s", "b"), ct));
        Assert.Equal(IntegrationErrorKind.Validation, mail.Kind);
        Assert.Equal("Microsoft 365 integration disabled", mail.Message);

        var teams = await Assert.ThrowsAsync<IntegrationException>(() => sp.GetRequiredService<ITeamsService>().SendAsync(
            new OutboundTeamsMessage("k", TeamsTargetKind.User, "t", UserId: "u"), ct));
        Assert.Equal(IntegrationErrorKind.Validation, teams.Kind);

        var cursor = new SyncCursor(IntegrationType.OutlookMail, "x|inbox", "delta", null, new DateTime(2026, 1, 1), null);
        var page = await sp.GetRequiredService<IMailSynchronizationService>().FetchAsync(cursor, ct);
        Assert.True(page.IsFinal);
        Assert.Empty(page.Messages);
        Assert.Equal(cursor.Watermark, page.Watermark);
        Assert.Empty(await sp.GetRequiredService<ITeamsSynchronizationService>().DiscoverSourcesAsync(ct));
        Assert.Empty(await sp.GetRequiredService<IUserDirectoryService>().ListUsersAsync(ct));

        var probe = await sp.GetRequiredService<IIntegrationHealth>().ProbeAsync(ct);
        Assert.Equal("Disabled", probe.Mode);
        Assert.False(probe.Authenticated);

        Assert.Null(sp.GetService<IDemoTenant>());
        Assert.Equal(Microsoft365Mode.Disabled, sp.GetRequiredService<IOptions<GraphOptions>>().Value.Mode);
    }

    [Fact]
    public void Emulator_defaults_derive_from_the_public_base_url()
    {
        var options = new GraphOptions { Mode = Microsoft365Mode.Emulator, PublicBaseUrl = "http://localhost:5860/" };
        options.ApplyDefaults();

        Assert.Equal("http://localhost:5860/graph-emulator/v1.0", options.ResolvedGraphBaseUrl);
        Assert.Equal("http://localhost:5860/graph-emulator/contoso-emulator/oauth2/v2.0/token", options.TokenEndpoint);
        Assert.Equal("followup@contoso.com", options.ServiceAccount);
        Assert.Equal(["followup@contoso.com"], options.ResolvedMonitoredMailboxes);

        var graph = new GraphOptions { Mode = Microsoft365Mode.Graph, TenantId = "t" };
        Assert.Equal("https://graph.microsoft.com/v1.0", graph.ResolvedGraphBaseUrl);
        Assert.Equal("https://login.microsoftonline.com/t/oauth2/v2.0/token", graph.TokenEndpoint);
    }
}
