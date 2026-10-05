using FollowUp.Core.Data;
using FollowUp.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Tests.Core.Queries;

/// <summary>
/// A small organisation used by the query and API tests: Finance and Development departments, two teams, a CEO,
/// an administrator, managers and members — all with logins (password <see cref="Password"/>).
/// </summary>
public sealed class TestOrg
{
    public const string Password = "Passw0rd!";

    /// <summary>Wednesday 7 Oct 2026, 11:30 in Asia/Kolkata.</summary>
    public static readonly DateTime Now = new(2026, 10, 7, 6, 0, 0, DateTimeKind.Utc);
    public static readonly DateOnly Today = new(2026, 10, 7);

    public Guid CalendarId { get; } = Guid.NewGuid();
    public Guid Finance { get; } = Guid.NewGuid();
    public Guid Development { get; } = Guid.NewGuid();
    public Guid FinanceTeam { get; } = Guid.NewGuid();
    public Guid DevTeam { get; } = Guid.NewGuid();
    public Guid Ceo { get; } = Guid.NewGuid();
    public Guid Admin { get; } = Guid.NewGuid();
    public Guid FinanceHead { get; } = Guid.NewGuid();
    public Guid FinanceMember { get; } = Guid.NewGuid();
    public Guid FinanceMember2 { get; } = Guid.NewGuid();
    public Guid DevLead { get; } = Guid.NewGuid();
    public Guid DevMember { get; } = Guid.NewGuid();

    public static readonly TestOrg Default = new();

    public async Task SeedAsync(FollowUpDbContext db, CancellationToken ct = default)
    {
        if (await db.Employees.AnyAsync(e => e.Id == Ceo, ct)) return;
        db.WorkCalendars.Add(new WorkCalendar { Id = CalendarId, Name = "India", TimeZoneId = "Asia/Kolkata", WeekendDays = "Saturday,Sunday", IsDefault = true });
        db.Departments.AddRange(
            new Department { Id = Finance, Code = "FIN", Name = "Finance", HeadId = null },
            new Department { Id = Development, Code = "DEV", Name = "Development" });

        Employee E(Guid id, string code, string name, string title, Guid? dept, Guid? manager = null) =>
            new() { Id = id, EmployeeCode = code, DisplayName = name, Email = $"{code.ToLowerInvariant()}@wings.example", JobTitle = title, DepartmentId = dept, ManagerId = manager };
        db.Employees.AddRange(
            E(Ceo, "CEO", "Asha Rao", "Chief Executive Officer", null),
            E(Admin, "ADM", "Ian Admin", "IT Administrator", null),
            E(FinanceHead, "FINH", "Farah Khan", "Finance Manager", Finance, Ceo),
            E(FinanceMember, "FIN1", "Raj Patel", "Accountant", Finance, FinanceHead),
            E(FinanceMember2, "FIN2", "Meera Iyer", "Accounts Executive", Finance, FinanceHead),
            E(DevLead, "DEVL", "Dev Lead", "Development Manager", Development, Ceo),
            E(DevMember, "DEV1", "Sam Coder", "Developer", Development, DevLead));
        await db.SaveChangesAsync(ct);

        (await db.Departments.FirstAsync(d => d.Id == Finance, ct)).HeadId = FinanceHead;
        (await db.Departments.FirstAsync(d => d.Id == Development, ct)).HeadId = DevLead;
        db.Teams.AddRange(
            new Team { Id = FinanceTeam, Name = "Finance Team", DepartmentId = Finance, LeaderId = FinanceHead },
            new Team { Id = DevTeam, Name = "Development Team", DepartmentId = Development, LeaderId = DevLead });
        db.TeamMembers.AddRange(
            new TeamMember { TeamId = FinanceTeam, EmployeeId = FinanceHead, Role = TeamMemberRole.Leader },
            new TeamMember { TeamId = FinanceTeam, EmployeeId = FinanceMember, Role = TeamMemberRole.Member },
            new TeamMember { TeamId = FinanceTeam, EmployeeId = FinanceMember2, Role = TeamMemberRole.Member },
            new TeamMember { TeamId = DevTeam, EmployeeId = DevLead, Role = TeamMemberRole.Leader },
            new TeamMember { TeamId = DevTeam, EmployeeId = DevMember, Role = TeamMemberRole.Member });

        var hasher = new PasswordHasher<UserAccount>();
        UserAccount A(Guid employee, string user, AppRole roles)
        {
            var a = new UserAccount { EmployeeId = employee, UserName = user, Roles = roles };
            a.PasswordHash = hasher.HashPassword(a, Password);
            return a;
        }
        db.UserAccounts.AddRange(
            A(Ceo, "ceo", AppRole.Executive),
            A(Admin, "admin", AppRole.Administrator),
            A(FinanceHead, "farah", AppRole.SeniorManager | AppRole.TeamLeader),
            A(FinanceMember, "raj", AppRole.TeamMember),
            A(FinanceMember2, "meera", AppRole.TeamMember),
            A(DevLead, "devlead", AppRole.TeamLeader),
            A(DevMember, "sam", AppRole.TeamMember));
        await db.SaveChangesAsync(ct);
    }
}
