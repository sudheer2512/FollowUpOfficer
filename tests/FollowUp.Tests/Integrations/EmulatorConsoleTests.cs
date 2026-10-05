using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using FollowUp.Integrations.GraphEmulator;
using static FollowUp.Tests.Integrations.EmulatorHost;

namespace FollowUp.Tests.Integrations;

public sealed class EmulatorConsoleTests : IAsyncLifetime
{
    private EmulatorHost _host = null!;

    public async Task InitializeAsync() => _host = await StartSeededAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Lists_the_directory_including_the_follow_up_officer()
    {
        var users = await _host.GetJsonAsync<List<SimUserDto>>("users");

        Assert.Equal(4, users.Count);
        Assert.Contains(users, u => u.Id == OfficerId && u.Mail == "followup@contoso.com");
        Assert.Contains(users, u => u.Id == Ravi.Id && u.ManagerId == Ceo.Id);
    }

    [Fact]
    public async Task Composed_mail_lands_in_mailboxes_and_replies_thread()
    {
        var sent = await _host.PostJsonAsync<SimMailSentDto>("mail",
            new SimComposeMailRequest(Ceo.Mail, [Ravi.Id], [Priya.Mail, "auditor@external.com"], "Vendor contract", "Please close this by Friday.",
                HighImportance: true));
        Assert.Equal([Ravi.Mail, Priya.Mail], sent.DeliveredTo);

        var inbox = await _host.GetJsonAsync<List<SimMailDto>>($"mailboxes/{Ravi.Mail}/messages?folder=inbox");
        var received = Assert.Single(inbox);
        Assert.Equal(sent.InternetMessageId, received.InternetMessageId);
        Assert.Equal("Please close this by Friday.", received.BodyText);
        Assert.Equal("high", received.Importance);
        Assert.Contains(received.Cc, c => c.Address == "auditor@external.com");
        Assert.Single(await _host.GetJsonAsync<List<SimMailDto>>($"mailboxes/{Ceo.Mail}/messages?folder=sentitems"));

        await _host.PostJsonAsync<SimMailSentDto>("mail",
            new SimComposeMailRequest(Ravi.Mail, [Ceo.Mail], null, null, "Will do.", InReplyToMessageId: received.Id));
        var reply = Assert.Single(await _host.GetJsonAsync<List<SimMailDto>>($"mailboxes/{Ceo.Mail}/messages"));
        Assert.Equal("RE: Vendor contract", reply.Subject);
        Assert.Equal(sent.InternetMessageId, reply.InReplyTo);
        Assert.Equal(sent.ConversationId, reply.ConversationId);
        Assert.Contains("Please close this by Friday.", reply.BodyText); // the console shows the full body including the quote
    }

    [Fact]
    public async Task Chats_and_channels_can_be_driven_as_any_user()
    {
        var chat = await _host.PostJsonAsync<SimChatDto>("chats/one-on-one", new SimOneOnOneRequest(Ceo.Mail, Priya.Id));
        var same = await _host.PostJsonAsync<SimChatDto>("chats/one-on-one", new SimOneOnOneRequest(Priya.Id, Ceo.Id));
        Assert.Equal(chat.Id, same.Id);
        Assert.Equal("oneOnOne", chat.ChatType);

        var posted = await _host.PostJsonAsync<SimPostResultDto>($"chats/{Uri.EscapeDataString(chat.Id)}/messages",
            new SimChatPostRequest(Priya.Id, "Can we move the review?", [Ceo.Id], Urgent: true));
        var chats = await _host.GetJsonAsync<List<SimChatDto>>($"chats?userId={Priya.Mail}");
        var listed = Assert.Single(chats, c => c.Id == chat.Id);
        Assert.Equal(posted.MessageId, listed.LastMessage!.Id);
        Assert.Equal("urgent", listed.LastMessage.Importance);
        Assert.Equal(Ceo.Id, Assert.Single(listed.LastMessage.Mentions).UserId);

        var root = await _host.PostJsonAsync<SimPostResultDto>($"teams/{TeamId}/channels/{Uri.EscapeDataString(ChannelId)}/messages",
            new SimChannelPostRequest(Ceo.Id, "Who owns the audit?", Subject: "Audit"));
        await _host.PostJsonAsync<SimPostResultDto>($"teams/{TeamId}/channels/{Uri.EscapeDataString(ChannelId)}/messages",
            new SimChannelPostRequest(Ravi.Id, "I do.", ReplyToId: root.MessageId));
        var thread = Assert.Single(await _host.GetJsonAsync<List<SimTeamsMessageDto>>(
            $"teams/{TeamId}/channels/{Uri.EscapeDataString(ChannelId)}/messages"));
        Assert.Equal("Audit", thread.Subject);
        Assert.Equal("I do.", Assert.Single(thread.Replies).BodyText);

        var channels = await _host.GetJsonAsync<List<SimChannelDto>>("channels");
        var general = Assert.Single(channels);
        Assert.Equal("Operations", general.TeamName);
        Assert.Equal(2, general.MessageCount);
    }

    [Fact]
    public async Task Faults_round_trip_and_are_validated()
    {
        var response = await _host.Console.PutAsJsonAsync("faults", new SimFaultsRequest(false, 25, false, 10));
        response.EnsureSuccessStatusCode();
        var faults = await _host.GetJsonAsync<SimFaultsDto>("faults");
        Assert.Equal(25, faults.ThrottlePercent);
        Assert.Equal(10, faults.LatencyMs);
        Assert.Equal(1, faults.DeltaEpoch);

        var invalid = await _host.Console.PutAsJsonAsync("faults", new SimFaultsRequest(false, 150, false, 0));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var unknown = await _host.Console.GetAsync("mailboxes/nobody@contoso.com/messages");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Graph_endpoints_require_an_emulator_token()
    {
        var client = _host.App.GetTestClient();

        var anonymous = await client.GetAsync("/graph-emulator/v1.0/users");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var error = await anonymous.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidAuthenticationToken", error.GetProperty("error").GetProperty("code").GetString());

        var token = await client.PostAsync("/graph-emulator/contoso-emulator/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "followup-officer-emulator",
            ["client_secret"] = "wrong",
            ["scope"] = "https://graph.microsoft.com/.default",
        }));
        Assert.Equal(HttpStatusCode.Unauthorized, token.StatusCode);
        Assert.Equal("invalid_client", (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }
}
