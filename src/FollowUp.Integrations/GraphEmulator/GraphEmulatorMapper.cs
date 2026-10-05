using FollowUp.Integrations.Graph;

namespace FollowUp.Integrations.GraphEmulator;

/// <summary>Maps emulator entities to Graph v1.0 JSON shapes.</summary>
internal static class GraphEmulatorMapper
{
    public static GraphUser User(EmulatorUser u, bool expandManager) => new()
    {
        Id = u.Id,
        DisplayName = u.DisplayName,
        Mail = u.Mail,
        UserPrincipalName = u.UserPrincipalName,
        JobTitle = u.JobTitle,
        Department = u.Department,
        AccountEnabled = u.AccountEnabled,
        Manager = expandManager && u.ManagerId is not null
            ? new GraphDirectoryObject { ODataType = "#microsoft.graph.user", Id = u.ManagerId }
            : null,
    };

    public static GraphMessage Mail(EmulatorMailItem m, bool includeExtendedProperties = false)
    {
        if (m.IsDeleted) return new GraphMessage { Id = m.Id, Removed = new GraphRemoved { Reason = "deleted" } };
        var from = new GraphRecipient { EmailAddress = new GraphEmailAddress { Address = m.FromAddress, Name = m.FromName ?? m.FromAddress } };
        return new GraphMessage
        {
            Id = m.Id,
            CreatedDateTime = m.CreatedDateTime,
            LastModifiedDateTime = m.LastModifiedDateTime,
            ReceivedDateTime = m.ReceivedDateTime,
            SentDateTime = m.SentDateTime,
            HasAttachments = m.HasAttachments,
            InternetMessageId = m.InternetMessageId,
            Subject = m.Subject,
            BodyPreview = m.BodyPreview,
            Importance = m.Importance,
            ParentFolderId = m.Folder,
            ConversationId = m.ConversationId,
            IsRead = m.IsRead,
            IsDraft = false,
            WebLink = $"https://outlook.office365.com/owa/?ItemID={Uri.EscapeDataString(m.Id)}&exvsurl=1&viewmodel=ReadMessageItem",
            Body = new GraphItemBody { ContentType = m.BodyContentType, Content = m.BodyContent },
            From = from,
            Sender = from,
            ToRecipients = Recipients(m.ToJson),
            CcRecipients = Recipients(m.CcJson),
            InternetMessageHeaders = GraphEmulatorStore.FromJson<MailHeader>(m.HeadersJson)
                .Select(h => new GraphInternetMessageHeader { Name = h.Name, Value = h.Value }).ToList(),
            SingleValueExtendedProperties = includeExtendedProperties
                ? GraphEmulatorStore.FromJson<MailExtendedProperty>(m.ExtendedPropertiesJson)
                    .Select(p => new GraphExtendedProperty { Id = p.Id, Value = p.Value }).ToList()
                : null,
        };
    }

    public static GraphChatMessage TeamsMessage(EmulatorTeamsMessage m)
    {
        var deleted = m.DeletedDateTime is not null;
        return new GraphChatMessage
        {
            Id = m.MessageId,
            ReplyToId = m.ReplyToId,
            Etag = m.LastModifiedDateTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MessageType = m.MessageType,
            CreatedDateTime = m.CreatedDateTime,
            LastModifiedDateTime = m.LastModifiedDateTime,
            DeletedDateTime = m.DeletedDateTime,
            Subject = m.Subject,
            ChatId = m.ChatId,
            ChannelIdentity = m.TeamId is null ? null : new GraphChannelIdentity { TeamId = m.TeamId, ChannelId = m.ChannelId },
            Importance = m.Importance,
            Locale = "en-us",
            WebUrl = m.ChatId is not null
                ? $"https://teams.microsoft.com/l/message/{Uri.EscapeDataString(m.ChatId)}/{m.MessageId}"
                : $"https://teams.microsoft.com/l/message/{Uri.EscapeDataString(m.ChannelId ?? "")}/{m.MessageId}?groupId={m.TeamId}&parentMessageId={m.ReplyToId ?? m.MessageId}",
            From = m.FromUserId is null
                ? null
                : new GraphIdentitySet { User = new GraphIdentity { Id = m.FromUserId, DisplayName = m.FromDisplayName, UserIdentityType = "aadUser" } },
            Body = new GraphItemBody { ContentType = m.BodyContentType, Content = deleted ? "" : m.BodyContent },
            Attachments = GraphEmulatorStore.FromJson<StoredTeamsAttachment>(m.AttachmentsJson)
                .Select(a => new GraphChatMessageAttachment { Id = a.Id, ContentType = a.ContentType, ContentUrl = a.ContentUrl, Name = a.Name })
                .ToList(),
            Mentions = deleted
                ? []
                : GraphEmulatorStore.FromJson<StoredMention>(m.MentionsJson).Select(x => new GraphChatMessageMention
                {
                    Id = x.Id,
                    MentionText = x.MentionText,
                    Mentioned = new GraphIdentitySet { User = new GraphIdentity { Id = x.UserId, DisplayName = x.DisplayName, UserIdentityType = "aadUser" } },
                }).ToList(),
        };
    }

    public static GraphChat Chat(EmulatorChat c, string tenantId, IEnumerable<EmulatorChatMember>? members, IReadOnlyDictionary<string, EmulatorUser>? users) => new()
    {
        Id = c.Id,
        Topic = c.Topic,
        ChatType = c.ChatType,
        CreatedDateTime = c.CreatedAt,
        LastUpdatedDateTime = c.LastUpdatedAt,
        TenantId = tenantId,
        WebUrl = $"https://teams.microsoft.com/l/chat/{Uri.EscapeDataString(c.Id)}/0?tenantId={tenantId}",
        Members = members?.Select(m =>
        {
            var user = users is not null && users.TryGetValue(m.UserId, out var u) ? u : null;
            return new GraphConversationMember
            {
                ODataType = "#microsoft.graph.aadUserConversationMember",
                Id = $"{c.Id}|{m.UserId}",
                DisplayName = user?.DisplayName,
                Roles = [m.Roles],
                UserId = m.UserId,
                Email = user?.Mail,
            };
        }).ToList(),
    };

    public static GraphGroup Group(EmulatorTeam t) => new()
    {
        Id = t.Id,
        DisplayName = t.DisplayName,
        Description = t.Description,
        ResourceProvisioningOptions = ["Team"],
    };

    public static GraphChannel Channel(EmulatorChannel c) => new()
    {
        Id = c.Id,
        DisplayName = c.DisplayName,
        Description = c.Description,
        MembershipType = "standard",
        CreatedDateTime = c.CreatedAt,
        WebUrl = $"https://teams.microsoft.com/l/channel/{Uri.EscapeDataString(c.Id)}/{Uri.EscapeDataString(c.DisplayName)}?groupId={c.TeamId}",
    };

    public static GraphEvent Event(EmulatorEvent e) => new()
    {
        Id = e.Id,
        TransactionId = e.TransactionId,
        Subject = e.Subject,
        Body = new GraphItemBody { ContentType = e.BodyContentType, Content = e.BodyContent },
        Start = new GraphDateTimeTimeZone { DateTime = e.StartDateTime, TimeZone = e.StartTimeZone },
        End = new GraphDateTimeTimeZone { DateTime = e.EndDateTime, TimeZone = e.EndTimeZone },
        IsAllDay = e.IsAllDay,
        Importance = e.Importance,
        IsReminderOn = e.IsReminderOn,
        ReminderMinutesBeforeStart = e.ReminderMinutesBeforeStart,
        ShowAs = e.ShowAs,
        CreatedDateTime = e.CreatedDateTime,
        LastModifiedDateTime = e.LastModifiedDateTime,
        WebLink = $"https://outlook.office365.com/owa/?itemid={Uri.EscapeDataString(e.Id)}&exvsurl=1&path=/calendar/item",
    };

    private static List<GraphRecipient> Recipients(string json) =>
        GraphEmulatorStore.FromJson<MailAddress>(json)
            .Select(r => new GraphRecipient { EmailAddress = new GraphEmailAddress { Address = r.Address, Name = r.Name ?? r.Address } })
            .ToList();
}
