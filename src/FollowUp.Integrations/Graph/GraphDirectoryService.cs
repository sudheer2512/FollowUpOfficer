using FollowUp.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations.Graph;

/// <summary>Reads Entra users (with manager) and Teams teams/channels for directory sync.</summary>
public sealed class GraphDirectoryService : IUserDirectoryService
{
    private const int MaxUsers = 50_000;
    private const int MaxTeams = 5_000;
    private const int MaxChannelsPerTeam = 1_000;

    private readonly GraphClient _graph;
    private readonly IOptions<GraphOptions> _options;

    public GraphDirectoryService(GraphClient graph, IOptions<GraphOptions> options)
    {
        _graph = graph;
        _options = options;
    }

    public async Task<IReadOnlyList<DirectoryUser>> ListUsersAsync(CancellationToken ct)
    {
        var url = "users?$select=id,displayName,mail,userPrincipalName,jobTitle,department,accountEnabled&$top=999";
        if (_options.Value.ExpandManager) url += "&$expand=" + Uri.EscapeDataString("manager($select=id)");
        var users = await _graph.GetAllAsync<GraphUser>(url, GraphAuth.Application, MaxUsers, ct);
        return users
            .Where(u => !string.IsNullOrEmpty(u.Id))
            .Select(u => new DirectoryUser(u.Id!, u.DisplayName ?? u.UserPrincipalName ?? u.Mail ?? u.Id!, u.Mail, u.UserPrincipalName,
                u.JobTitle, u.Department, u.AccountEnabled ?? true, u.Manager?.Id))
            .ToList();
    }

    public async Task<IReadOnlyList<DirectoryTeam>> ListTeamsAsync(CancellationToken ct)
    {
        var filter = Uri.EscapeDataString("resourceProvisioningOptions/Any(x:x eq 'Team')");
        var groups = await _graph.GetAllAsync<GraphGroup>(
            $"groups?$filter={filter}&$select=id,displayName,resourceProvisioningOptions&$top=999", GraphAuth.Application, MaxTeams, ct);
        var teams = new List<DirectoryTeam>(groups.Count);
        foreach (var group in groups.Where(g => !string.IsNullOrEmpty(g.Id)))
        {
            var channels = await _graph.GetAllAsync<GraphChannel>(
                $"teams/{GraphJson.Segment(group.Id!)}/channels?$select=id,displayName", GraphAuth.Application, MaxChannelsPerTeam, ct);
            teams.Add(new DirectoryTeam(group.Id!, group.DisplayName ?? group.Id!,
                channels.Where(c => !string.IsNullOrEmpty(c.Id)).Select(c => new DirectoryChannel(c.Id!, c.DisplayName ?? c.Id!)).ToList()));
        }
        return teams;
    }
}
