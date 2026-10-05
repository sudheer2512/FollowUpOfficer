using System.Text.Json.Serialization;

namespace FollowUp.Integrations.Graph;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// Microsoft Graph v1.0 resource shapes (subset). Shared by the Graph client (deserialisation) and the
// Graph emulator (serialisation) so both sides agree on the wire format.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>OData collection page: value + nextLink / deltaLink.</summary>
internal sealed class GraphCollection<T>
{
    [JsonPropertyName("@odata.context")] public string? Context { get; set; }
    [JsonPropertyName("value")] public List<T> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
    [JsonPropertyName("@odata.deltaLink")] public string? DeltaLink { get; set; }
}

internal sealed class GraphRemoved
{
    public string? Reason { get; set; }
}

internal sealed class GraphEmailAddress
{
    public string? Address { get; set; }
    public string? Name { get; set; }
}

internal sealed class GraphRecipient
{
    public GraphEmailAddress? EmailAddress { get; set; }
}

internal sealed class GraphItemBody
{
    public string? ContentType { get; set; }
    public string? Content { get; set; }
}

internal sealed class GraphInternetMessageHeader
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

internal sealed class GraphExtendedProperty
{
    public string Id { get; set; } = "";
    public string? Value { get; set; }
}

/// <summary>Outlook message.</summary>
internal sealed class GraphMessage
{
    public string? Id { get; set; }
    public DateTimeOffset? CreatedDateTime { get; set; }
    public DateTimeOffset? LastModifiedDateTime { get; set; }
    public DateTimeOffset? ReceivedDateTime { get; set; }
    public DateTimeOffset? SentDateTime { get; set; }
    public bool? HasAttachments { get; set; }
    public string? InternetMessageId { get; set; }
    public string? Subject { get; set; }
    public string? BodyPreview { get; set; }
    public string? Importance { get; set; }
    public string? ParentFolderId { get; set; }
    public string? ConversationId { get; set; }
    public bool? IsRead { get; set; }
    public bool? IsDraft { get; set; }
    public string? WebLink { get; set; }
    public GraphItemBody? Body { get; set; }
    public GraphRecipient? Sender { get; set; }
    public GraphRecipient? From { get; set; }
    public List<GraphRecipient>? ToRecipients { get; set; }
    public List<GraphRecipient>? CcRecipients { get; set; }
    public List<GraphInternetMessageHeader>? InternetMessageHeaders { get; set; }
    public List<GraphExtendedProperty>? SingleValueExtendedProperties { get; set; }
    [JsonPropertyName("@removed")] public GraphRemoved? Removed { get; set; }
}

internal sealed class GraphAttachment
{
    [JsonPropertyName("@odata.type")] public string? ODataType { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? ContentType { get; set; }
    public long? Size { get; set; }
    public bool? IsInline { get; set; }
}

internal sealed class GraphIdentity
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? UserIdentityType { get; set; }
    public string? ApplicationIdentityType { get; set; }
}

internal sealed class GraphIdentitySet
{
    public GraphIdentity? User { get; set; }
    public GraphIdentity? Application { get; set; }
}

internal sealed class GraphChatMessageMention
{
    public int Id { get; set; }
    public string? MentionText { get; set; }
    public GraphIdentitySet? Mentioned { get; set; }
}

internal sealed class GraphChatMessageAttachment
{
    public string? Id { get; set; }
    public string? ContentType { get; set; }
    public string? ContentUrl { get; set; }
    public string? Content { get; set; }
    public string? Name { get; set; }
}

internal sealed class GraphChannelIdentity
{
    public string? TeamId { get; set; }
    public string? ChannelId { get; set; }
}

/// <summary>Teams chat or channel message.</summary>
internal sealed class GraphChatMessage
{
    public string? Id { get; set; }
    public string? ReplyToId { get; set; }
    public string? Etag { get; set; }
    public string? MessageType { get; set; }
    public DateTimeOffset? CreatedDateTime { get; set; }
    public DateTimeOffset? LastModifiedDateTime { get; set; }
    public DateTimeOffset? LastEditedDateTime { get; set; }
    public DateTimeOffset? DeletedDateTime { get; set; }
    public string? Subject { get; set; }
    public string? Summary { get; set; }
    public string? ChatId { get; set; }
    public GraphChannelIdentity? ChannelIdentity { get; set; }
    public string? Importance { get; set; }
    public string? Locale { get; set; }
    public string? WebUrl { get; set; }
    public GraphIdentitySet? From { get; set; }
    public GraphItemBody? Body { get; set; }
    public List<GraphChatMessageAttachment>? Attachments { get; set; }
    public List<GraphChatMessageMention>? Mentions { get; set; }
    [JsonPropertyName("@removed")] public GraphRemoved? Removed { get; set; }
}

internal sealed class GraphDirectoryObject
{
    [JsonPropertyName("@odata.type")] public string? ODataType { get; set; }
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
}

internal sealed class GraphUser
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? Mail { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public bool? AccountEnabled { get; set; }
    public GraphDirectoryObject? Manager { get; set; }
}

internal sealed class GraphGroup
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public List<string>? ResourceProvisioningOptions { get; set; }
}

internal sealed class GraphChannel
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? MembershipType { get; set; }
    public string? WebUrl { get; set; }
    public DateTimeOffset? CreatedDateTime { get; set; }
}

internal sealed class GraphConversationMember
{
    [JsonPropertyName("@odata.type")] public string? ODataType { get; set; }
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public List<string>? Roles { get; set; }
    public string? UserId { get; set; }
    public string? Email { get; set; }
}

internal sealed class GraphChat
{
    public string? Id { get; set; }
    public string? Topic { get; set; }
    public string? ChatType { get; set; }
    public DateTimeOffset? CreatedDateTime { get; set; }
    public DateTimeOffset? LastUpdatedDateTime { get; set; }
    public string? TenantId { get; set; }
    public string? WebUrl { get; set; }
    public List<GraphConversationMember>? Members { get; set; }
}

internal sealed class GraphDateTimeTimeZone
{
    public string? DateTime { get; set; }
    public string? TimeZone { get; set; }
}

internal sealed class GraphEvent
{
    public string? Id { get; set; }
    public string? TransactionId { get; set; }
    public string? Subject { get; set; }
    public GraphItemBody? Body { get; set; }
    public GraphDateTimeTimeZone? Start { get; set; }
    public GraphDateTimeTimeZone? End { get; set; }
    public bool? IsAllDay { get; set; }
    public string? Importance { get; set; }
    public bool? IsReminderOn { get; set; }
    public int? ReminderMinutesBeforeStart { get; set; }
    public string? ShowAs { get; set; }
    public DateTimeOffset? CreatedDateTime { get; set; }
    public DateTimeOffset? LastModifiedDateTime { get; set; }
    public string? WebLink { get; set; }
}

internal sealed class GraphErrorResponse
{
    public GraphErrorDetail? Error { get; set; }
}

internal sealed class GraphErrorDetail
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public GraphInnerError? InnerError { get; set; }
}

internal sealed class GraphInnerError
{
    public string? Date { get; set; }
    [JsonPropertyName("request-id")] public string? RequestId { get; set; }
    [JsonPropertyName("client-request-id")] public string? ClientRequestId { get; set; }
}

/// <summary>OAuth 2.0 token endpoint response (success or error).</summary>
internal sealed class TokenEndpointResponse
{
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    [JsonPropertyName("ext_expires_in")] public int? ExtExpiresIn { get; set; }
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}
