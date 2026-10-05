using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Integrations.GraphEmulator;
using static FollowUp.Tests.Integrations.EmulatorHost;

namespace FollowUp.Tests.Integrations;

public sealed class EmulatorMailTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private EmulatorHost _host = null!;

    public async Task InitializeAsync() => _host = await StartSeededAsync(s => s["Microsoft365:PageSize"] = "2");

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static SyncCursor Cursor(string sourceKey) => new(IntegrationType.OutlookMail, sourceKey, null, null, null, null);

    [Fact]
    public async Task Reminder_mail_round_trips_through_inbox_delta()
    {
        var email = _host.Get<IEmailService>();
        var sync = _host.Get<IMailSynchronizationService>();
        var since = DateTime.UtcNow.AddMinutes(-5);

        var sent = await email.SendAsync(new OutboundEmail("task-42:reminder:1", [new EmailAddress(Ravi.Mail, Ravi.DisplayName)],
            [new EmailAddress(Ceo.Mail)], "FT-42 update needed", "Please share the status of FT-42.", HighImportance: true), Ct);
        Assert.True(sent.Success);
        Assert.NotNull(sent.ExternalMessageId);

        // First round: the reminder is in Ravi's inbox with our key.
        var (first, cursor, _) = await DrainAsync(sync, Cursor($"{Ravi.Mail}|inbox"));
        var message = Assert.Single(first);
        Assert.Equal(sent.ExternalMessageId, message.ExternalId);
        Assert.Matches("^<[0-9a-f]{32}@followup\\.emulator>$", message.ExternalId);
        Assert.StartsWith("AAMk", message.GraphItemId);
        Assert.Equal("task-42:reminder:1", message.Headers["X-FollowUp-Key"]);
        Assert.Equal(CommunicationChannel.Email, message.Channel);
        Assert.Equal("mail", message.ConversationKey);
        Assert.Equal($"{Ravi.Mail}|inbox", message.SourceKey);
        Assert.False(string.IsNullOrEmpty(message.ConversationId));
        Assert.Equal("followup@contoso.com", message.FromAddress);
        Assert.Equal("high", message.Importance);
        Assert.Equal("Please share the status of FT-42.", message.BodyText);
        Assert.Equal(Ravi.Mail, Assert.Single(message.To).Address);
        Assert.Equal(Ceo.Mail, Assert.Single(message.Cc).Address);
        Assert.NotNull(cursor.DeltaToken);
        Assert.NotNull(cursor.Watermark);

        // Second round from the saved deltaLink: nothing new.
        var (second, cursor2, _) = await DrainAsync(sync, cursor);
        Assert.Empty(second);

        // A new mail appears in the next round, and only it.
        await _host.TenantAsync(t => t.SendMailAsync(
            new TenantMail(Ceo.Mail, Ceo.Mail, [Ravi.Mail], [], "Budget", "Need the budget today.", DateTime.UtcNow), Ct));
        var (third, _, _) = await DrainAsync(sync, cursor2);
        var budget = Assert.Single(third);
        Assert.Equal("Budget", budget.Subject);
        Assert.Equal(Ceo.Mail, budget.FromAddress);
        Assert.False(budget.Headers.ContainsKey("X-FollowUp-Key"));

        // Reconciliation finds the key in Sent Items.
        Assert.True(await email.WasSentAsync("task-42:reminder:1", since, Ct));
        Assert.False(await email.WasSentAsync("task-42:reminder:2", since, Ct));
        Assert.False(await email.WasSentAsync("task-42:reminder:1", DateTime.UtcNow.AddMinutes(30), Ct));
    }

    [Fact]
    public async Task Delta_rounds_page_with_next_links()
    {
        for (var i = 1; i <= 5; i++)
        {
            var n = i;
            await _host.TenantAsync(t => t.SendMailAsync(
                new TenantMail(Ceo.Mail, Ceo.Mail, [Priya.Mail], [], $"Item {n}", $"Body {n}", DateTime.UtcNow.AddMinutes(-10 + n)), Ct));
        }

        var (messages, cursor, pages) = await DrainAsync(_host.Get<IMailSynchronizationService>(), Cursor($"{Priya.Mail}|inbox"));

        Assert.Equal(3, pages); // page size 2
        Assert.Equal(["Item 1", "Item 2", "Item 3", "Item 4", "Item 5"], messages.Select(m => m.Subject));
        Assert.Equal(5, messages.Select(m => m.ExternalId).Distinct().Count());
        Assert.NotNull(cursor.DeltaToken);
        Assert.Null(cursor.ContinuationToken);
    }

    [Fact]
    public async Task Invalidated_delta_token_is_gone_and_resync_from_watermark_recovers()
    {
        var sync = _host.Get<IMailSynchronizationService>();
        await _host.TenantAsync(t => t.SendMailAsync(new TenantMail(Ceo.Mail, Ceo.Mail, [Ravi.Mail], [], "Hello", "Hi", DateTime.UtcNow), Ct));
        var (_, cursor, _) = await DrainAsync(sync, Cursor($"{Ravi.Mail}|inbox"));

        var invalidated = await _host.PostJsonAsync<SimDeltaInvalidatedDto>("delta/invalidate", new { });
        Assert.True(invalidated.DeltaEpoch > 1);

        var ex = await Assert.ThrowsAsync<IntegrationException>(() => sync.FetchAsync(cursor, Ct));
        Assert.Equal(IntegrationErrorKind.Gone, ex.Kind);
        Assert.Equal(410, ex.StatusCode);
        Assert.Contains("syncStateNotFound", ex.Message);

        // Core's recovery: drop the tokens and resync from the watermark (overlap may re-deliver; Core dedupes).
        var (again, recovered, _) = await DrainAsync(sync, cursor with { DeltaToken = null, ContinuationToken = null });
        Assert.Equal("Hello", Assert.Single(again).Subject);
        Assert.NotNull(recovered.DeltaToken);
    }

    [Fact]
    public async Task First_sync_window_uses_watermark_or_initial_days()
    {
        var now = DateTime.UtcNow;
        await _host.TenantAsync(async t =>
        {
            await t.SendMailAsync(new TenantMail(Ceo.Mail, Ceo.Mail, [Ravi.Mail], [], "Old", "Old mail", now.AddDays(-30)), Ct);
            await t.SendMailAsync(new TenantMail(Ceo.Mail, Ceo.Mail, [Ravi.Mail], [], "Recent", "Recent mail", now.AddDays(-1)), Ct);
        });
        var sync = _host.Get<IMailSynchronizationService>();

        var (initial, _, _) = await DrainAsync(sync, Cursor($"{Ravi.Mail}|inbox"));
        Assert.Equal(["Recent"], initial.Select(m => m.Subject));

        var (fromWatermark, _, _) = await DrainAsync(sync, Cursor($"{Ravi.Mail}|inbox") with { Watermark = now.AddDays(-40) });
        Assert.Equal(["Old", "Recent"], fromWatermark.Select(m => m.Subject));
    }

    [Fact]
    public async Task Sent_items_carry_the_key_and_replies_thread_with_quotes_stripped()
    {
        var email = _host.Get<IEmailService>();
        var sync = _host.Get<IMailSynchronizationService>();
        var sources = await sync.DiscoverSourcesAsync(Ct);
        Assert.Equal(["followup@contoso.com|inbox", "followup@contoso.com|sentitems"], sources.Select(s => s.SourceKey));

        var sent = await email.SendAsync(new OutboundEmail("task-7:reminder:3", [new EmailAddress(Ravi.Mail)], [], "FT-7: status?",
            "Please share the status of FT-7.", BodyHtml: "<p>Please share the status of <b>FT-7</b>.</p>"), Ct);

        var (sentItems, _, _) = await DrainAsync(sync, Cursor("followup@contoso.com|sentitems"));
        var sentItem = Assert.Single(sentItems);
        Assert.Equal("task-7:reminder:3", sentItem.Headers["X-FollowUp-Key"]);
        Assert.Equal("Please share the status of FT-7.", sentItem.BodyText);

        // Ravi replies from the simulator; Outlook-style quoting of the original is added.
        var reply = await _host.PostJsonAsync<SimMailSentDto>("mail", new SimComposeMailRequest(Ravi.Mail, ["followup@contoso.com"], null, null,
            "Done. Uploaded the reconciliation.", sent.ExternalMessageId));
        Assert.Contains("followup@contoso.com", reply.DeliveredTo);

        var (inbox, _, _) = await DrainAsync(sync, Cursor("followup@contoso.com|inbox"));
        var inbound = Assert.Single(inbox);
        Assert.Equal("RE: FT-7: status?", inbound.Subject);
        Assert.Equal(sent.ExternalMessageId, inbound.Headers["In-Reply-To"]);
        Assert.Contains(sent.ExternalMessageId!, inbound.Headers["References"]);
        Assert.Equal(sentItem.ConversationId, inbound.ConversationId);
        Assert.Equal("Done. Uploaded the reconciliation.", inbound.BodyText);
    }
}
