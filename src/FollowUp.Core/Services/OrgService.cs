using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FollowUp.Core.Services;

/// <summary>
/// Organisation administration (spec §1, §2, §34, §71, §73): departments, teams and members, employees and their
/// logins, working calendars and holidays, escalation policies, runtime settings and the Entra directory sync.
/// Administrators only.
/// </summary>
public sealed class OrgService(
    FollowUpDbContext db,
    ActorContext actor,
    IClock clock,
    CalendarProvider calendars,
    AuditService audit,
    SettingsService settings,
    IUserDirectoryService directory,
    IntegrationErrorRecorder errors,
    ILogger<OrgService> logger)
{
    public const int MinPasswordLength = 8;
    private static readonly PasswordHasher<UserAccount> Hasher = new();

    private void RequireAdmin()
    {
        if (!actor.IsSystem && !actor.Viewer.IsAdmin) throw new ForbiddenException("Only administrators can change the organisation.");
    }

    private static string Required(string? value, string code, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ValidationException(code, $"{label} is required.");
        return value.Trim();
    }

    // ═══════════════════════════════ Departments ═══════════════════════════════

    public async Task<List<DepartmentDto>> DepartmentsAsync(CancellationToken ct, Guid? id = null)
    {
        var q = db.Departments.AsNoTracking();
        if (id is { } x) q = q.Where(d => d.Id == x);
        var rows = await q.OrderBy(d => d.Name).Select(d => new
        {
            d.Id, d.Code, d.Name, d.HeadId, d.ParentDepartmentId, d.IsActive,
            HeadName = d.Head != null ? d.Head.DisplayName : null, HeadTitle = d.Head != null ? d.Head.JobTitle : null,
            HeadEmail = d.Head != null ? d.Head.Email : null,
            Employees = db.Employees.Count(e => e.DepartmentId == d.Id && e.IsActive),
            Teams = db.Teams.Count(t => t.DepartmentId == d.Id && t.IsActive),
        }).ToListAsync(ct);
        return rows.Select(d => new DepartmentDto(d.Id, d.Code, d.Name, d.HeadId is { } h ? new PersonRef(h, d.HeadName ?? "", d.HeadTitle, d.HeadEmail) : null,
            d.ParentDepartmentId, d.IsActive, d.Employees, d.Teams)).ToList();
    }

    public async Task<DepartmentDto> DepartmentAsync(Guid id, CancellationToken ct) =>
        (await DepartmentsAsync(ct, id)).FirstOrDefault() ?? throw new NotFoundException("Department");

    public async Task<DepartmentDto> SaveDepartmentAsync(Guid? id, DepartmentInput input, CancellationToken ct)
    {
        RequireAdmin();
        var code = Required(input.Code, "code_required", "Code").ToUpperInvariant();
        var name = Required(input.Name, "name_required", "Name");
        var dept = id is { } x ? await db.Departments.FirstOrDefaultAsync(d => d.Id == x, ct) ?? throw new NotFoundException("Department") : new Department();
        if (await db.Departments.AnyAsync(d => d.Code == code && d.Id != dept.Id, ct)) throw new ConflictException("duplicate_code", $"Department code {code} is already used.");
        if (input.HeadId is { } head && !await db.Employees.AnyAsync(e => e.Id == head, ct)) throw new ValidationException("head_invalid", "Department head not found.");
        if (input.ParentDepartmentId is { } parent)
        {
            if (parent == dept.Id) throw new ValidationException("parent_invalid", "A department cannot be its own parent.");
            for (Guid? p = parent; p is { } cur;)
            {
                var row = await db.Departments.AsNoTracking().Where(d => d.Id == cur).Select(d => new { d.ParentDepartmentId }).FirstOrDefaultAsync(ct)
                    ?? throw new ValidationException("parent_invalid", "Parent department not found.");
                if (row.ParentDepartmentId == dept.Id) throw new ValidationException("parent_cycle", "That parent would create a cycle.");
                p = row.ParentDepartmentId;
            }
        }
        dept.Code = code;
        dept.Name = name;
        dept.HeadId = input.HeadId;
        dept.ParentDepartmentId = input.ParentDepartmentId;
        dept.IsActive = input.IsActive;
        if (id is null) db.Departments.Add(dept);
        audit.Record(id is null ? "DepartmentCreated" : "DepartmentUpdated", nameof(Department), dept.Id, details: $"{code} {name}");
        await db.SaveChangesAsync(ct);
        return await DepartmentAsync(dept.Id, ct);
    }

    // ═══════════════════════════════ Teams ═══════════════════════════════

    public async Task<List<TeamDto>> TeamsAsync(CancellationToken ct, Guid? id = null)
    {
        var q = db.Teams.AsNoTracking();
        if (id is { } x) q = q.Where(t => t.Id == x);
        var teams = await q.Include(t => t.Members).ThenInclude(m => m.Employee).OrderBy(t => t.Name).ToListAsync(ct);
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var leaderIds = teams.SelectMany(t => new[] { t.LeaderId, t.DeputyLeaderId }).Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        var leaders = await db.Employees.AsNoTracking().Where(e => leaderIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new PersonRef(e.Id, e.DisplayName, e.JobTitle, e.Email), ct);
        return teams.Select(t => new TeamDto(t.Id, t.Name,
            t.DepartmentId is { } d && depts.TryGetValue(d, out var dn) ? new NamedRef(d, dn) : null,
            t.LeaderId is { } l && leaders.TryGetValue(l, out var lp) ? lp : null,
            t.DeputyLeaderId is { } dl && leaders.TryGetValue(dl, out var dp) ? dp : null,
            t.Email, t.TeamsTeamId, t.TeamsChannelId, t.TeamsChannelName, t.IsActive,
            t.Members.OrderByDescending(m => m.Role == TeamMemberRole.Leader).ThenByDescending(m => m.Role == TeamMemberRole.Deputy)
                .ThenBy(m => m.Employee?.DisplayName)
                .Select(m => new TeamMemberDto(m.EmployeeId, m.Employee?.DisplayName ?? "", m.Employee?.JobTitle, m.Role)).ToList())).ToList();
    }

    public async Task<TeamDto> TeamAsync(Guid id, CancellationToken ct) => (await TeamsAsync(ct, id)).FirstOrDefault() ?? throw new NotFoundException("Team");

    public async Task<TeamDto> SaveTeamAsync(Guid? id, TeamInput input, CancellationToken ct)
    {
        RequireAdmin();
        var name = Required(input.Name, "name_required", "Name");
        var team = id is { } x ? await db.Teams.Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == x, ct) ?? throw new NotFoundException("Team") : new Team();
        if (await db.Teams.AnyAsync(t => t.Name == name && t.Id != team.Id, ct)) throw new ConflictException("duplicate_name", $"A team named {name} already exists.");
        if (input.DepartmentId is { } dep && !await db.Departments.AnyAsync(d => d.Id == dep, ct)) throw new ValidationException("department_invalid", "Department not found.");
        var wanted = new Dictionary<Guid, TeamMemberRole>();
        foreach (var m in input.MemberIds ?? []) wanted[m] = TeamMemberRole.Member;
        if (input.DeputyLeaderId is { } deputy) wanted[deputy] = TeamMemberRole.Deputy;
        if (input.LeaderId is { } leader) wanted[leader] = TeamMemberRole.Leader;
        var ids = wanted.Keys.ToList();
        var existing = await db.Employees.Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct);
        if (existing.Count != ids.Count) throw new ValidationException("member_invalid", "One or more team members were not found.");

        team.Name = name;
        team.DepartmentId = input.DepartmentId;
        team.LeaderId = input.LeaderId;
        team.DeputyLeaderId = input.DeputyLeaderId;
        team.Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim();
        team.TeamsTeamId = string.IsNullOrWhiteSpace(input.TeamsTeamId) ? null : input.TeamsTeamId.Trim();
        team.TeamsChannelId = string.IsNullOrWhiteSpace(input.TeamsChannelId) ? null : input.TeamsChannelId.Trim();
        team.TeamsChannelName = string.IsNullOrWhiteSpace(input.TeamsChannelName) ? null : input.TeamsChannelName.Trim();
        team.IsActive = input.IsActive;
        var now = clock.UtcNow;
        foreach (var m in team.Members.ToList())
        {
            if (!wanted.TryGetValue(m.EmployeeId, out var role)) db.TeamMembers.Remove(m);
            else m.Role = role;
        }
        foreach (var (empId, role) in wanted.Where(w => team.Members.All(m => m.EmployeeId != w.Key)))
            team.Members.Add(new TeamMember { TeamId = team.Id, EmployeeId = empId, Role = role, JoinedAt = now });
        if (id is null) db.Teams.Add(team);
        audit.Record(id is null ? "TeamCreated" : "TeamUpdated", nameof(Team), team.Id, details: $"{name} ({wanted.Count} member(s))");
        await db.SaveChangesAsync(ct);
        return await TeamAsync(team.Id, ct);
    }

    // ═══════════════════════════════ Employees & logins ═══════════════════════════════

    public async Task<List<EmployeeDto>> EmployeesAsync(CancellationToken ct, Guid? id = null)
    {
        var q = db.Employees.AsNoTracking();
        if (id is { } x) q = q.Where(e => e.Id == x);
        var rows = await q.OrderBy(e => e.DisplayName).Select(e => new
        {
            e.Id, e.EmployeeCode, e.DisplayName, e.Email, e.JobTitle, e.Phone, e.DepartmentId, e.ManagerId, e.IsActive, e.ExternalUserId, e.PreferredChannels,
            Dept = e.Department != null ? e.Department.Name : null,
            ManagerName = e.Manager != null ? e.Manager.DisplayName : null,
            ManagerTitle = e.Manager != null ? e.Manager.JobTitle : null,
            Teams = e.TeamMemberships.Select(m => new { m.TeamId, Name = m.Team!.Name }).ToList(),
            Account = e.Account == null ? null : new { e.Account.UserName, e.Account.Roles, e.Account.IsEnabled, e.Account.LastLoginAt, HasPassword = e.Account.PasswordHash != null || e.Account.EntraObjectId != null },
        }).ToListAsync(ct);
        return rows.Select(e => new EmployeeDto(e.Id, e.EmployeeCode, e.DisplayName, e.Email, e.JobTitle, e.Phone,
            e.DepartmentId is { } d ? new NamedRef(d, e.Dept ?? "") : null,
            e.ManagerId is { } m ? new PersonRef(m, e.ManagerName ?? "", e.ManagerTitle) : null,
            e.Teams.OrderBy(t => t.Name).Select(t => new NamedRef(t.TeamId, t.Name)).ToList(),
            e.IsActive, e.Account?.UserName, e.Account?.Roles ?? AppRole.None, e.Account is not null, e.Account?.IsEnabled ?? false, e.Account?.LastLoginAt,
            e.ExternalUserId, e.PreferredChannels)).ToList();
    }

    public async Task<EmployeeDto> EmployeeAsync(Guid id, CancellationToken ct) => (await EmployeesAsync(ct, id)).FirstOrDefault() ?? throw new NotFoundException("Employee");

    public async Task<EmployeeDto> SaveEmployeeAsync(Guid? id, EmployeeInput input, CancellationToken ct)
    {
        RequireAdmin();
        var code = Required(input.EmployeeCode, "code_required", "Employee code");
        var name = Required(input.DisplayName, "name_required", "Name");
        var email = Required(input.Email, "email_required", "Email").ToLowerInvariant();
        if (!email.Contains('@')) throw new ValidationException("email_invalid", "Enter a valid email address.");
        var emp = id is { } x ? await db.Employees.FirstOrDefaultAsync(e => e.Id == x, ct) ?? throw new NotFoundException("Employee") : new Employee();
        if (await db.Employees.AnyAsync(e => e.Email.ToLower() == email && e.Id != emp.Id, ct)) throw new ConflictException("duplicate_email", $"{email} is already used by another employee.");
        if (await db.Employees.AnyAsync(e => e.EmployeeCode == code && e.Id != emp.Id, ct)) throw new ConflictException("duplicate_code", $"Employee code {code} is already used.");
        if (input.DepartmentId is { } dep && !await db.Departments.AnyAsync(d => d.Id == dep, ct)) throw new ValidationException("department_invalid", "Department not found.");
        if (input.ManagerId is { } mgr)
        {
            if (mgr == emp.Id) throw new ValidationException("manager_invalid", "A person cannot be their own manager.");
            if (!await db.Employees.AnyAsync(e => e.Id == mgr, ct)) throw new ValidationException("manager_invalid", "Manager not found.");
        }
        emp.EmployeeCode = code;
        emp.DisplayName = name;
        emp.Email = email;
        emp.JobTitle = string.IsNullOrWhiteSpace(input.JobTitle) ? null : input.JobTitle.Trim();
        emp.Phone = string.IsNullOrWhiteSpace(input.Phone) ? null : input.Phone.Trim();
        emp.DepartmentId = input.DepartmentId;
        emp.ManagerId = input.ManagerId;
        emp.IsActive = input.IsActive;
        emp.PreferredChannels = input.PreferredChannels | NotifyChannels.InApp;
        if (id is null) db.Employees.Add(emp);
        audit.Record(id is null ? "EmployeeCreated" : "EmployeeUpdated", nameof(Employee), emp.Id, details: $"{code} {name}");
        await db.SaveChangesAsync(ct);
        return await EmployeeAsync(emp.Id, ct);
    }

    /// <summary>Creates or updates the employee's login. A password / role / enable change bumps the security stamp (signs out sessions).</summary>
    public async Task<EmployeeDto> SetAccountAsync(Guid employeeId, AccountInput input, CancellationToken ct)
    {
        RequireAdmin();
        var emp = await db.Employees.Include(e => e.Account).FirstOrDefaultAsync(e => e.Id == employeeId, ct) ?? throw new NotFoundException("Employee");
        var userName = Required(input.UserName, "username_required", "User name");
        var lower = userName.ToLowerInvariant();
        if (await db.UserAccounts.AnyAsync(a => a.UserName.ToLower() == lower && a.EmployeeId != employeeId, ct))
            throw new ConflictException("duplicate_username", $"User name {userName} is already taken.");
        var roles = input.Roles == AppRole.None ? AppRole.TeamMember : input.Roles;
        if (employeeId == actor.EmployeeId && (!input.Enabled || !roles.HasFlag(AppRole.Administrator)))
            throw new ValidationException("self_lockout", "You cannot disable your own login or remove your own Administrator role.");
        if (input.NewPassword is { Length: > 0 } pw && pw.Length < MinPasswordLength)
            throw new ValidationException("password_too_short", $"Passwords need at least {MinPasswordLength} characters.");

        var account = emp.Account;
        var isNew = account is null;
        account ??= new UserAccount { EmployeeId = emp.Id };
        var stampChange = isNew || account.Roles != roles || account.IsEnabled != input.Enabled || !string.IsNullOrEmpty(input.NewPassword);
        account.UserName = userName;
        account.Roles = roles;
        account.IsEnabled = input.Enabled;
        if (!string.IsNullOrEmpty(input.NewPassword))
        {
            account.PasswordHash = Hasher.HashPassword(account, input.NewPassword);
            account.FailedLogins = 0;
            account.LockedUntil = null;
        }
        if (stampChange) account.SecurityStamp++;
        if (isNew) db.UserAccounts.Add(account);
        audit.Record(isNew ? "AccountCreated" : "AccountUpdated", nameof(UserAccount), account.Id, null, "Roles", null, roles.ToString(),
            $"{userName}; enabled={input.Enabled}{(string.IsNullOrEmpty(input.NewPassword) ? "" : "; password reset")}");
        await db.SaveChangesAsync(ct);
        return await EmployeeAsync(employeeId, ct);
    }

    // ═══════════════════════════════ Working calendars (spec §73) ═══════════════════════════════

    public async Task<List<CalendarDto>> CalendarsAsync(CancellationToken ct, Guid? id = null)
    {
        var q = db.WorkCalendars.AsNoTracking().Include(c => c.Holidays).AsQueryable();
        if (id is { } x) q = q.Where(c => c.Id == x);
        var rows = await q.OrderByDescending(c => c.IsDefault).ThenBy(c => c.Name).ToListAsync(ct);
        return rows.Select(c => new CalendarDto(c.Id, c.Name, c.Region, c.TimeZoneId, c.WeekendDays, c.WorkdayStart, c.WorkdayEnd, c.IsDefault,
            c.Holidays.OrderBy(h => h.Date).Select(h => new HolidayDto(h.Id, h.Date, h.Name, h.Kind)).ToList())).ToList();
    }

    public async Task<CalendarDto> CalendarAsync(Guid id, CancellationToken ct) => (await CalendarsAsync(ct, id)).FirstOrDefault() ?? throw new NotFoundException("Calendar");

    public async Task<CalendarDto> SaveCalendarAsync(Guid? id, CalendarInput input, CancellationToken ct)
    {
        RequireAdmin();
        var name = Required(input.Name, "name_required", "Name");
        var tz = Required(input.TimeZoneId, "timezone_required", "Time zone");
        if (BusinessCalendar.ResolveTimeZone(tz) == TimeZoneInfo.Utc && !tz.Equals("UTC", StringComparison.OrdinalIgnoreCase) && !tz.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("timezone_invalid", $"Unknown time zone '{tz}'.");
        var weekend = BusinessCalendar.ParseWeekend(input.WeekendDays).Distinct().ToList();
        if (weekend.Count >= 7) throw new ValidationException("weekend_invalid", "At least one day must be a working day.");
        if (input.WorkdayEnd <= input.WorkdayStart) throw new ValidationException("workday_invalid", "The working day must end after it starts.");
        var cal = id is { } x ? await db.WorkCalendars.FirstOrDefaultAsync(c => c.Id == x, ct) ?? throw new NotFoundException("Calendar") : new WorkCalendar();
        cal.Name = name;
        cal.Region = string.IsNullOrWhiteSpace(input.Region) ? null : input.Region.Trim();
        cal.TimeZoneId = tz;
        cal.WeekendDays = string.Join(",", weekend);
        cal.WorkdayStart = input.WorkdayStart;
        cal.WorkdayEnd = input.WorkdayEnd;
        var anyDefault = await db.WorkCalendars.AnyAsync(c => c.IsDefault && c.Id != cal.Id, ct);
        cal.IsDefault = input.IsDefault || !anyDefault;
        if (cal.IsDefault)
            foreach (var other in await db.WorkCalendars.Where(c => c.IsDefault && c.Id != cal.Id).ToListAsync(ct)) other.IsDefault = false;
        if (id is null) db.WorkCalendars.Add(cal);
        audit.Record(id is null ? "CalendarCreated" : "CalendarUpdated", nameof(WorkCalendar), cal.Id, details: $"{name} ({tz}, weekend {cal.WeekendDays})");
        await db.SaveChangesAsync(ct);
        calendars.Invalidate();
        return await CalendarAsync(cal.Id, ct);
    }

    public async Task<CalendarDto> AddHolidayAsync(Guid calendarId, HolidayInput input, CancellationToken ct)
    {
        RequireAdmin();
        var name = Required(input.Name, "name_required", "Holiday name");
        if (!await db.WorkCalendars.AnyAsync(c => c.Id == calendarId, ct)) throw new NotFoundException("Calendar");
        var existing = await db.Holidays.FirstOrDefaultAsync(h => h.CalendarId == calendarId && h.Date == input.Date, ct);
        if (existing is not null) { existing.Name = name; existing.Kind = input.Kind; }
        else db.Holidays.Add(new Holiday { CalendarId = calendarId, Date = input.Date, Name = name, Kind = input.Kind });
        audit.Record("HolidaySaved", nameof(WorkCalendar), calendarId, details: $"{input.Date:yyyy-MM-dd} {name}");
        await db.SaveChangesAsync(ct);
        calendars.Invalidate();
        return await CalendarAsync(calendarId, ct);
    }

    public async Task<CalendarDto> RemoveHolidayAsync(Guid calendarId, Guid holidayId, CancellationToken ct)
    {
        RequireAdmin();
        var h = await db.Holidays.FirstOrDefaultAsync(x => x.Id == holidayId && x.CalendarId == calendarId, ct) ?? throw new NotFoundException("Holiday");
        db.Holidays.Remove(h);
        audit.Record("HolidayRemoved", nameof(WorkCalendar), calendarId, details: $"{h.Date:yyyy-MM-dd} {h.Name}");
        await db.SaveChangesAsync(ct);
        calendars.Invalidate();
        return await CalendarAsync(calendarId, ct);
    }

    // ═══════════════════════════════ Escalation policies (spec §34) ═══════════════════════════════

    public async Task<List<EscalationPolicyDto>> PoliciesAsync(CancellationToken ct, Guid? id = null)
    {
        var q = db.EscalationPolicies.AsNoTracking().Include(p => p.Steps).AsQueryable();
        if (id is { } x) q = q.Where(p => p.Id == x);
        var rows = await q.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync(ct);
        return rows.Select(p => new EscalationPolicyDto(p.Id, p.Name, p.Description, p.AppliesTo, p.IsDefault, p.IsActive,
            p.Steps.OrderBy(s => s.Order).Select(s => new EscalationStepDto(s.Id, s.Order, s.Trigger, s.Threshold, s.Unit, s.Targets, s.Channels, s.Level,
                s.RaiseCeoAttention, Describe(s))).ToList())).ToList();
    }

    private static string Describe(EscalationStep s) =>
        $"{Text.Humanize(s.Trigger)} {(s.Trigger == EscalationTrigger.OnDue ? "" : EscalationEvaluator.Describe(s.Threshold, s.Unit) + " ")}→ level {s.Level}".Replace("  ", " ");

    public async Task<EscalationPolicyDto> PolicyAsync(Guid id, CancellationToken ct) => (await PoliciesAsync(ct, id)).FirstOrDefault() ?? throw new NotFoundException("Escalation policy");

    public async Task<EscalationPolicyDto> SavePolicyAsync(Guid? id, EscalationPolicyInput input, CancellationToken ct)
    {
        RequireAdmin();
        var name = Required(input.Name, "name_required", "Name");
        if ((input.Steps ?? []).Count == 0) throw new ValidationException("steps_required", "Add at least one escalation step.");
        foreach (var s in input.Steps!)
        {
            if (s.Threshold < 0) throw new ValidationException("threshold_invalid", "Thresholds cannot be negative.");
            if (s.Level is < 1 or > 5) throw new ValidationException("level_invalid", "Escalation levels run from 1 (assignee) to 5 (CEO).");
            if (s.Targets == EscalationTarget.None) throw new ValidationException("targets_required", "Each step needs at least one target.");
        }
        var appliesTo = string.Join(",", (input.AppliesTo ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Enum.TryParse<Criticality>(x, true, out var c) ? c.ToString() : throw new ValidationException("applies_to_invalid", $"Unknown criticality '{x}'."))
            .Distinct());
        var policy = id is { } pid ? await db.EscalationPolicies.Include(p => p.Steps).FirstOrDefaultAsync(p => p.Id == pid, ct) ?? throw new NotFoundException("Escalation policy")
            : new EscalationPolicy();
        if (await db.EscalationPolicies.AnyAsync(p => p.Name == name && p.Id != policy.Id, ct)) throw new ConflictException("duplicate_name", $"A policy named {name} already exists.");
        policy.Name = name;
        policy.Description = input.Description;
        policy.AppliesTo = appliesTo;
        policy.IsDefault = input.IsDefault;
        policy.IsActive = input.IsActive;
        foreach (var old in policy.Steps.ToList()) db.EscalationSteps.Remove(old);
        if (id is null) db.EscalationPolicies.Add(policy);
        var order = 1;
        foreach (var s in input.Steps.OrderBy(s => s.Order))
            db.EscalationSteps.Add(new EscalationStep
            {
                PolicyId = policy.Id, Order = order++, Trigger = s.Trigger, Threshold = s.Threshold, Unit = s.Unit, Targets = s.Targets,
                Channels = s.Channels == NotifyChannels.None ? NotifyChannels.Email | NotifyChannels.Teams : s.Channels, Level = s.Level, RaiseCeoAttention = s.RaiseCeoAttention,
            });
        if (policy.IsDefault)
            foreach (var other in await db.EscalationPolicies.Where(p => p.IsDefault && p.Id != policy.Id).ToListAsync(ct)) other.IsDefault = false;
        audit.Record(id is null ? "EscalationPolicyCreated" : "EscalationPolicyUpdated", nameof(EscalationPolicy), policy.Id, details: $"{name}: {input.Steps.Count} step(s)");
        await db.SaveChangesAsync(ct);
        return await PolicyAsync(policy.Id, ct);
    }

    // ═══════════════════════════════ Settings ═══════════════════════════════

    public Task<RuntimeSettings> SettingsAsync(CancellationToken ct) => settings.GetAsync(ct);

    public Task<RuntimeSettings> SaveSettingsAsync(RuntimeSettings s, CancellationToken ct)
    {
        RequireAdmin();
        return settings.UpdateAsync(s, ct);
    }

    // ═══════════════════════════════ Directory sync (spec §71) ═══════════════════════════════

    /// <summary>
    /// Matches Entra / Microsoft 365 users to employees by email (or UPN): stores the external user id (needed for
    /// Teams messages) and fills blank job titles; matches Teams teams by name. Never creates or deletes anyone.
    /// </summary>
    public async Task<int> SyncDirectoryAsync(CancellationToken ct)
    {
        RequireAdmin();
        IReadOnlyList<DirectoryUser> users;
        try { users = await directory.ListUsersAsync(ct); }
        catch (IntegrationException ex)
        {
            await errors.RecordAsync("Directory", null, "ListUsers", ex, ct);
            await db.SaveChangesAsync(ct);
            throw;
        }
        var employees = await db.Employees.ToListAsync(ct);
        var byEmail = employees.GroupBy(e => e.Email.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var changed = 0;
        foreach (var u in users)
        {
            var key = (u.Mail ?? u.UserPrincipalName)?.Trim().ToLowerInvariant();
            if (key is null || !byEmail.TryGetValue(key, out var emp))
            {
                var upn = u.UserPrincipalName?.Trim().ToLowerInvariant();
                if (upn is null || !byEmail.TryGetValue(upn, out emp)) continue;
            }
            var touched = false;
            if (!string.IsNullOrWhiteSpace(u.ExternalId) && emp.ExternalUserId != u.ExternalId) { emp.ExternalUserId = u.ExternalId; touched = true; }
            if (string.IsNullOrWhiteSpace(emp.JobTitle) && !string.IsNullOrWhiteSpace(u.JobTitle)) { emp.JobTitle = u.JobTitle.Trim(); touched = true; }
            if (touched) changed++;
        }

        try
        {
            var remoteTeams = await directory.ListTeamsAsync(ct);
            var teams = await db.Teams.ToListAsync(ct);
            foreach (var rt in remoteTeams)
            {
                var team = teams.FirstOrDefault(t => string.Equals(t.Name, rt.DisplayName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.TeamsTeamId, rt.TeamId, StringComparison.OrdinalIgnoreCase));
                if (team is null) continue;
                var touched = false;
                if (string.IsNullOrEmpty(team.TeamsTeamId)) { team.TeamsTeamId = rt.TeamId; touched = true; }
                if (string.IsNullOrEmpty(team.TeamsChannelId) && team.TeamsTeamId == rt.TeamId && rt.Channels.Count > 0)
                {
                    var channel = rt.Channels.FirstOrDefault(c => c.DisplayName.Equals("General", StringComparison.OrdinalIgnoreCase)) ?? rt.Channels[0];
                    team.TeamsChannelId = channel.ChannelId;
                    team.TeamsChannelName = channel.DisplayName;
                    touched = true;
                }
                if (touched) changed++;
            }
        }
        catch (IntegrationException ex)
        {
            await errors.RecordAsync("Directory", null, "ListTeams", ex, ct);
            logger.LogWarning(ex, "Teams directory listing failed; users were still synchronised");
        }
        if (changed > 0) audit.Record("DirectorySynced", "Directory", null, details: $"{changed} record(s) updated from {users.Count} directory user(s)");
        await db.SaveChangesAsync(ct);
        return changed;
    }
}
