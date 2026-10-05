namespace FollowUp.Domain;

/// <summary>A person in the organisation directory (spec §1 "Employees"). May or may not have an application login.</summary>
public sealed class Employee : Entity, IStamped
{
    public string EmployeeCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? Phone { get; set; }
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }
    /// <summary>Line manager — the default "accountable manager" and an escalation target.</summary>
    public Guid? ManagerId { get; set; }
    public Employee? Manager { get; set; }
    public Guid? CalendarId { get; set; }
    /// <summary>Microsoft Graph / Entra object id — used for Teams messaging and directory sync.</summary>
    public string? ExternalUserId { get; set; }
    /// <summary>Preferred reminder channels; criticality can widen them.</summary>
    public NotifyChannels PreferredChannels { get; set; } = NotifyChannels.Email | NotifyChannels.Teams | NotifyChannels.InApp;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public UserAccount? Account { get; set; }
    public List<TeamMember> TeamMemberships { get; set; } = [];
}

/// <summary>Application login (spec §1 "Users"). One-to-one with an employee.</summary>
public sealed class UserAccount : Entity, IStamped
{
    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public string UserName { get; set; } = "";
    public string? PasswordHash { get; set; }
    /// <summary>Entra ID object id (oid claim) for SSO sign-in.</summary>
    public string? EntraObjectId { get; set; }
    public AppRole Roles { get; set; } = AppRole.TeamMember;
    public bool IsEnabled { get; set; } = true;
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    /// <summary>Bumped on password/role change to invalidate existing sessions.</summary>
    public int SecurityStamp { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class Department : Entity, IStamped
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid? HeadId { get; set; }
    public Employee? Head { get; set; }
    public Guid? ParentDepartmentId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A team (spec §2). A person can belong to multiple teams.</summary>
public sealed class Team : Entity, IStamped
{
    public string Name { get; set; } = "";
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public Guid? LeaderId { get; set; }
    public Employee? Leader { get; set; }
    public Guid? DeputyLeaderId { get; set; }
    public Employee? DeputyLeader { get; set; }
    /// <summary>Email address / distribution list.</summary>
    public string? Email { get; set; }
    /// <summary>Microsoft Teams team id (Graph group id) and channel id used for channel notifications and sync.</summary>
    public string? TeamsTeamId { get; set; }
    public string? TeamsChannelId { get; set; }
    public string? TeamsChannelName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<TeamMember> Members { get; set; } = [];
}

public sealed class TeamMember : Entity
{
    public Guid TeamId { get; set; }
    public Team? Team { get; set; }
    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public TeamMemberRole Role { get; set; }
    public DateTime JoinedAt { get; set; }
}

/// <summary>Working-day calendar (spec §73): weekend days, time zone and holidays.</summary>
public sealed class WorkCalendar : Entity, IStamped
{
    public string Name { get; set; } = "";
    public string? Region { get; set; }
    /// <summary>IANA or Windows time zone id.</summary>
    public string TimeZoneId { get; set; } = "Asia/Kolkata";
    /// <summary>Comma-separated weekend days, e.g. "Saturday,Sunday" or "Friday,Saturday".</summary>
    public string WeekendDays { get; set; } = "Saturday,Sunday";
    public TimeOnly WorkdayStart { get; set; } = new(9, 30);
    public TimeOnly WorkdayEnd { get; set; } = new(18, 30);
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<Holiday> Holidays { get; set; } = [];
}

public sealed class Holiday : Entity
{
    public Guid CalendarId { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
    public HolidayKind Kind { get; set; }
}
