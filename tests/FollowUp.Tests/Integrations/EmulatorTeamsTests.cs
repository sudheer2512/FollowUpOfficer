using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Integrations.GraphEmulator;
using static FollowUp.Tests.Integrations.EmulatorHost;

namespace FollowUp.Tests.Integrations;

public sealed class EmulatorTeamsTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string ChannelKey = $"{TeamId}|{ChannelId}";
    private EmulatorHost _host = null!;

    public async Task InitializeAsync() => _host = await StartSeededAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Chat_sync_returns_messages_after_the_watermark_in_ascending_order()
    {
        var now = DateTime.UtcNow;
        var chatId = await _host.TenantAsync(t => t.EnsureOneOnOneChatAsync(Ceo.Id, Ravi.Id, Ct));
        await _host.TenantAsync(async t =>
        {
            await t.PostChatMessageAsync(chatId, new TenantPost(Ceo.Id, "Ten minutes ago", now.AddMinutes(-10)), Ct);
            await t.PostChatMessageAsync(chatId, new TenantPost(Ravi.Id, "Five minutes ago", now.AddMinutes(-5)), Ct);
            await t.PostChatMessageAsync(chatId, new TenantPost(Ceo.Id, "One minute ago @Ravi Kumar", now.AddMinutes(-1), MentionUserIds: [Ravi.Id]), Ct);
        });
        var sync = _host.Get<ITeamsSynchronizationService>();

        var page = await sync.FetchAsync(new SyncCursor(IntegrationType.TeamsChat, chatId, null, null, now.AddMinutes(-6), null), Ct);

        Assert.True(page.IsFinal);
        Assert.Null(page.ContinuationToken);
        Assert.Equal(["Five minutes ago", "One minute ago @Ravi Kumar"], page.Messages.Select(m => m.BodyText));
        var last = page.Messages[^1];
        Assert.Equal(CommunicationChannel.TeamsChat, last.Channel);
        Assert.Equal(chatId, last.ConversationKey);
        Assert.Equal(chatId, last.ConversationId);
        Assert.Equal(Ceo.Id, last.FromUserId);
        Assert.Equal(Ceo.DisplayName, last.FromName);
        Assert.Equal(Ravi.Id, Assert.Single(last.Mentions).ExternalUserId);
        Assert.Equal(last.LastModifiedAt, page.Watermark);
        Assert.Matches("^[0-9]{13}$", last.ExternalId);

        // The next round overlaps by two minutes: the newest message may come again (Core dedupes), older ones do not.
        var again = await sync.FetchAsync(new SyncCursor(IntegrationType.TeamsChat, chatId, null, null, page.Watermark, null), Ct);
        Assert.Equal([last.ExternalId], again.Messages.Select(m => m.ExternalId));
    }

    [Fact]
    public async Task Chat_sync_pages_through_all_newer_messages()
    {
        var start = DateTime.UtcNow.AddHours(-2);
        await _host.TenantAsync(async t =>
        {
            for (var i = 0; i < 60; i++)
                await t.PostChatMessageAsync(GroupChatId, new TenantPost(i % 2 == 0 ? Ravi.Id : Priya.Id, $"Update {i:00}", start.AddMinutes(i)), Ct);
        });

        var page = await _host.Get<ITeamsSynchronizationService>()
            .FetchAsync(new SyncCursor(IntegrationType.TeamsChat, GroupChatId, null, null, null, null), Ct);

        Assert.True(page.IsFinal);
        Assert.Equal(Enumerable.Range(0, 60).Select(i => $"Update {i:00}"), page.Messages.Select(m => m.BodyText));
        Assert.Equal(start.AddMinutes(59), page.Watermark!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Bounded_chat_sync_continues_on_the_next_call_without_advancing_the_watermark_early()
    {
        await using var host = await StartSeededAsync(s => s["Microsoft365:MaxChatMessagesPerFetch"] = "50");
        var start = DateTime.UtcNow.AddHours(-2);
        await host.TenantAsync(async t =>
        {
            for (var i = 0; i < 60; i++)
                await t.PostChatMessageAsync(GroupChatId, new TenantPost(Ravi.Id, $"Update {i:00}", start.AddMinutes(i)), Ct);
        });
        var sync = host.Get<ITeamsSynchronizationService>();
        var cursor = new SyncCursor(IntegrationType.TeamsChat, GroupChatId, null, null, null, null);

        var first = await sync.FetchAsync(cursor, Ct);
        Assert.False(first.IsFinal);
        Assert.Equal(50, first.Messages.Count);
        Assert.Null(first.Watermark);
        Assert.NotNull(first.ContinuationToken);

        var second = await sync.FetchAsync(cursor with { ContinuationToken = first.ContinuationToken }, Ct);
        Assert.True(second.IsFinal);
        Assert.Equal(10, second.Messages.Count);
        Assert.Equal(60, first.Messages.Concat(second.Messages).Select(m => m.ExternalId).Distinct().Count());
        Assert.Equal(start.AddMinutes(59), second.Watermark!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Channel_delta_returns_roots_with_their_replies()
    {
        var now = DateTime.UtcNow;
        var root1 = await _host.TenantAsync(t => t.PostChannelMessageAsync(TeamId, ChannelId, new TenantPost(Ceo.Id, "Q3 numbers?", now.AddMinutes(-30)), Ct));
        var reply0 = await _host.TenantAsync(t =>
            t.PostChannelMessageAsync(TeamId, ChannelId, new TenantPost(Priya.Id, "Need a day", now.AddMinutes(-25), ReplyToId: root1), Ct));
        var reply1 = await _host.TenantAsync(t =>
            t.PostChannelMessageAsync(TeamId, ChannelId, new TenantPost(Ravi.Id, "Working on it", now.AddMinutes(-20), ReplyToId: root1), Ct));
        var sync = _host.Get<ITeamsSynchronizationService>();

        var (round1, cursor, _) = await DrainAsync(sync, new SyncCursor(IntegrationType.TeamsChannel, ChannelKey, null, null, null, null));
        Assert.Equal([root1, reply0, reply1], round1.Select(m => m.ExternalId));
        Assert.Equal(now.AddMinutes(-20), cursor.Watermark!.Value, TimeSpan.FromMilliseconds(1));
        var root = round1[0];
        Assert.Equal(CommunicationChannel.TeamsChannel, root.Channel);
        Assert.Equal(ChannelKey, root.ConversationKey);
        Assert.Equal($"{ChannelKey}|{root1}", root.ConversationId);
        Assert.Null(root.ThreadRootId);
        Assert.Null(root.ReplyToId);
        var reply = round1[2];
        Assert.Equal(root1, reply.ThreadRootId);
        Assert.Equal(root1, reply.ReplyToId);
        Assert.Equal($"{ChannelKey}|{root1}", reply.ConversationId);
        Assert.Equal("Working on it", reply.BodyText);
        Assert.NotNull(cursor.DeltaToken);

        var (round2, cursor2, _) = await DrainAsync(sync, cursor);
        Assert.Empty(round2);

        var reply2 = await _host.TenantAsync(t =>
            t.PostChannelMessageAsync(TeamId, ChannelId, new TenantPost(Priya.Id, "Uploaded to SharePoint", DateTime.UtcNow, ReplyToId: root1), Ct));
        var root2 = await _host.TenantAsync(t =>
            t.PostChannelMessageAsync(TeamId, ChannelId, new TenantPost(Ceo.Id, "Board pack ready?", DateTime.UtcNow), Ct));

        var (round3, _, _) = await DrainAsync(sync, cursor2);
        Assert.Contains(round3, m => m.ExternalId == root1 && m.ThreadRootId is null); // root re-reported because its thread changed
        Assert.Contains(round3, m => m.ExternalId == reply2 && m.ThreadRootId == root1 && m.FromUserId == Priya.Id);
        Assert.Contains(round3, m => m.ExternalId == root2 && m.ThreadRootId is null);
        Assert.DoesNotContain(round3, m => m.ExternalId == reply0); // older than the watermark minus the overlap
    }

    [Fact]
    public async Task One_on_one_send_creates_the_chat_once_and_posts_into_it()
    {
        var teams = _host.Get<ITeamsService>();

        var first = await teams.SendAsync(new OutboundTeamsMessage("k1", TeamsTargetKind.User, "Please update FT-9", UserId: Ravi.Id, Urgent: true,
            Mentions: [new TeamsMention(Ravi.Id, Ravi.DisplayName)]), Ct);
        var second = await teams.SendAsync(new OutboundTeamsMessage("k2", TeamsTargetKind.User, "Second reminder", UserAddress: Ravi.Mail), Ct);

        Assert.True(first.Success);
        Assert.NotNull(first.ExternalMessageId);
        var chats = await _host.GetJsonAsync<List<SimChatDto>>($"chats?userId={Ravi.Id}");
        var chat = Assert.Single(chats, c => c.ChatType == "oneOnOne");
        Assert.Equal(GraphEmulatorIds.OneOnOneChat(OfficerId, Ravi.Id), chat.Id);
        Assert.Equal(new[] { OfficerId, Ravi.Id }.Order(), chat.Members.Select(m => m.UserId).Order());

        var messages = await _host.GetJsonAsync<List<SimTeamsMessageDto>>($"chats/{Uri.EscapeDataString(chat.Id)}/messages");
        Assert.Equal([first.ExternalMessageId, second.ExternalMessageId], messages.Select(m => m.Id));
        Assert.All(messages, m => Assert.Equal(OfficerId, m.FromUserId));
        Assert.Equal("urgent", messages[0].Importance);
        Assert.Equal("normal", messages[1].Importance);
        Assert.Equal(Ravi.Id, Assert.Single(messages[0].Mentions).UserId);
        Assert.Equal("@Ravi Kumar Please update FT-9", messages[0].BodyText);
    }

    [Fact]
    public async Task Channel_group_chat_and_activity_feed_sends()
    {
        var teams = _host.Get<ITeamsService>();

        var channel = await teams.SendAsync(new OutboundTeamsMessage("k3", TeamsTargetKind.Channel, "Weekly review at 5 pm", TeamId: TeamId,
            ChannelId: ChannelId), Ct);
        var group = await teams.SendAsync(new OutboundTeamsMessage("k4", TeamsTargetKind.GroupChat, "3 tasks are overdue", Html: "<p>3 tasks are <b>overdue</b></p>",
            ChatId: GroupChatId), Ct);
        var activity = await teams.SendAsync(new OutboundTeamsMessage("k5", TeamsTargetKind.ActivityFeed, "FT-12 is overdue", UserId: Priya.Id), Ct);

        var channelMessages = await _host.GetJsonAsync<List<SimTeamsMessageDto>>(
            $"teams/{TeamId}/channels/{Uri.EscapeDataString(ChannelId)}/messages");
        Assert.Contains(channelMessages, m => m.Id == channel.ExternalMessageId && m.FromUserId == OfficerId && m.BodyText == "Weekly review at 5 pm");

        var groupMessages = await _host.GetJsonAsync<List<SimTeamsMessageDto>>($"chats/{Uri.EscapeDataString(GroupChatId)}/messages");
        Assert.Contains(groupMessages, m => m.Id == group.ExternalMessageId && m.BodyText == "3 tasks are overdue");

        Assert.True(activity.Success);
        Assert.Null(activity.ExternalMessageId);
        var feed = await _host.GetJsonAsync<List<SimActivityDto>>($"activity?userId={Priya.Id}");
        var item = Assert.Single(feed);
        Assert.Equal("systemDefault", item.ActivityType);
        Assert.Equal("FT-12 is overdue", item.Text);
        Assert.Equal(Priya.DisplayName, item.UserName);
    }

    [Fact]
    public async Task Posting_to_a_chat_the_service_account_is_not_in_is_a_permission_error()
    {
        const string privateChat = "19:private-pair@thread.v2";
        await _host.TenantAsync(t => t.EnsureDirectoryAsync([], [], [new TenantChat(privateChat, "group", "Private", [Ceo.Id, Ravi.Id])], Ct));

        var ex = await Assert.ThrowsAsync<IntegrationException>(() => _host.Get<ITeamsService>().SendAsync(
            new OutboundTeamsMessage("k6", TeamsTargetKind.GroupChat, "hello", ChatId: privateChat), Ct));

        Assert.Equal(IntegrationErrorKind.Permission, ex.Kind);
    }

    [Fact]
    public async Task Discovers_service_account_chats_and_all_channels()
    {
        await _host.Get<ITeamsService>().SendAsync(new OutboundTeamsMessage("k7", TeamsTargetKind.User, "hi", UserId: Ravi.Id), Ct);

        var sources = await _host.Get<ITeamsSynchronizationService>().DiscoverSourcesAsync(Ct);

        Assert.Contains(sources, s => s.Type == IntegrationType.TeamsChat && s.SourceKey == GroupChatId && s.DisplayName == "Leadership");
        Assert.Contains(sources, s => s.Type == IntegrationType.TeamsChat && s.DisplayName == "Chat with Ravi Kumar");
        Assert.Contains(sources, s => s.Type == IntegrationType.TeamsChannel && s.SourceKey == ChannelKey && s.DisplayName == "Operations / General");
    }
}
