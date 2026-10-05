namespace FollowUp.Domain.Rules;

/// <summary>The signed-in person and the organisational scope their roles cover.</summary>
public sealed record Viewer(
    Guid EmployeeId,
    AppRole Roles,
    IReadOnlySet<Guid> TeamIds,
    IReadOnlySet<Guid> LedTeamIds,
    IReadOnlySet<Guid> ManagedDepartmentIds)
{
    public bool IsExecutive => Roles.HasFlag(AppRole.Executive);
    public bool IsAdmin => Roles.HasFlag(AppRole.Administrator);
    public bool IsSeniorManager => Roles.HasFlag(AppRole.SeniorManager);
    public bool IsTeamLeader => Roles.HasFlag(AppRole.TeamLeader) || LedTeamIds.Count > 0;
    public bool IsManagement => IsExecutive || IsSeniorManager || IsTeamLeader;

    public static Viewer System { get; } = new(Guid.Empty, AppRole.Executive | AppRole.Administrator, new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>());
}

/// <summary>Facts about a task needed to decide visibility.</summary>
public sealed record TaskAccessFacts(
    TaskVisibility Visibility,
    Guid? DepartmentId,
    IReadOnlyCollection<Guid> TeamIds,
    /// <summary>Delegators, creator and assignees — always see the task.</summary>
    IReadOnlyCollection<Guid> CoreParticipantIds,
    /// <summary>Followers, accountable manager and approvers.</summary>
    IReadOnlyCollection<Guid> OtherParticipantIds);

/// <summary>
/// Task-level visibility (spec §3, §70). The same rules are applied to imported emails and Teams messages
/// through the tasks they are linked to. Core translates this into an EF query filter; tests keep both in step.
/// </summary>
public static class TaskAccess
{
    public static bool CanView(Viewer v, TaskAccessFacts t)
    {
        if (v.IsExecutive) return true;                                     // complete visibility
        if (t.CoreParticipantIds.Contains(v.EmployeeId)) return true;
        var otherParticipant = t.OtherParticipantIds.Contains(v.EmployeeId);
        var managesDept = v.IsSeniorManager && t.DepartmentId is { } d && v.ManagedDepartmentIds.Contains(d);
        var leadsTeam = t.TeamIds.Any(v.LedTeamIds.Contains);
        var inTeam = t.TeamIds.Any(v.TeamIds.Contains);

        return t.Visibility switch
        {
            TaskVisibility.Normal => otherParticipant || managesDept || leadsTeam || v.IsAdmin,
            TaskVisibility.TeamOnly => otherParticipant || managesDept || leadsTeam || inTeam,
            TaskVisibility.ManagementOnly => (otherParticipant && v.IsManagement) || managesDept || leadsTeam,
            TaskVisibility.CeoOnly => false,
            TaskVisibility.SelectedParticipants => otherParticipant,
            _ => false,
        };
    }

    /// <summary>Can change definition, responsibility and deadlines.</summary>
    public static bool CanManage(Viewer v, Guid? delegatorId, Guid? originalDelegatorId, Guid? createdById, Guid? accountableManagerId,
        Guid? departmentId, IReadOnlyCollection<Guid> teamIds) =>
        v.IsExecutive
        || v.EmployeeId == delegatorId || v.EmployeeId == originalDelegatorId || v.EmployeeId == createdById
        || v.EmployeeId == accountableManagerId
        || (v.IsSeniorManager && departmentId is { } d && v.ManagedDepartmentIds.Contains(d))
        || teamIds.Any(v.LedTeamIds.Contains);
}
