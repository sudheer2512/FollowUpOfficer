using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Core.Services;

/// <summary>Sign-in failed (HTTP 401) with a stable code: invalid_credentials, account_locked, account_disabled, no_account.</summary>
public sealed class AuthenticationFailedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>An authenticated session: the account, its employee and the current security stamp.</summary>
public sealed record SessionInfo(Guid AccountId, Guid EmployeeId, string Name, string UserName, AppRole Roles, int SecurityStamp);

/// <summary>
/// Application sign-in (spec §1 "Users", §69): local passwords hashed with ASP.NET Core Identity's PBKDF2
/// <see cref="PasswordHasher{TUser}"/>, lockout after repeated failures, Entra ID mapping (oid or email/UPN, no
/// auto-provisioning), and per-request session validation through the security stamp.
/// </summary>
public sealed class AccountService(FollowUpDbContext db, IClock clock, AuditService audit, ActorContext actor)
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly PasswordHasher<UserAccount> Hasher = new();

    public async Task<SessionInfo> LoginAsync(string? userName, string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            throw new AuthenticationFailedException("invalid_credentials", "Enter your user name and password.");
        var lower = userName.Trim().ToLowerInvariant();
        var account = await db.UserAccounts.Include(a => a.Employee).FirstOrDefaultAsync(a => a.UserName.ToLower() == lower, ct);
        var now = clock.UtcNow;
        if (account is null)
        {
            Hasher.VerifyHashedPassword(new UserAccount(), DummyHash, password);   // equalise timing
            throw new AuthenticationFailedException("invalid_credentials", "The user name or password is incorrect.");
        }
        if (account.LockedUntil is { } until && until > now)
            throw new AuthenticationFailedException("account_locked", $"Too many failed attempts. Try again after {Math.Ceiling((until - now).TotalMinutes):0} minute(s).");

        var result = account.PasswordHash is null ? PasswordVerificationResult.Failed : Hasher.VerifyHashedPassword(account, account.PasswordHash, password);
        using (actor.AsSystem(ChangeSource.User))
        {
            if (result == PasswordVerificationResult.Failed)
            {
                account.FailedLogins++;
                var locked = account.FailedLogins >= MaxFailedLogins;
                if (locked)
                {
                    account.LockedUntil = now + LockoutDuration;
                    account.FailedLogins = 0;
                }
                audit.Record(locked ? "AccountLocked" : "LoginFailed", nameof(UserAccount), account.Id, details: account.UserName);
                await db.SaveChangesAsync(ct);
                throw locked
                    ? new AuthenticationFailedException("account_locked", $"Too many failed attempts. The account is locked for {LockoutDuration.TotalMinutes:0} minutes.")
                    : new AuthenticationFailedException("invalid_credentials", "The user name or password is incorrect.");
            }
            // Disabled accounts are only reported once the password is right, so the response never reveals which accounts exist.
            if (!account.IsEnabled || account.Employee is not { IsActive: true })
                throw new AuthenticationFailedException("account_disabled", "This account is disabled. Contact your administrator.");
            if (result == PasswordVerificationResult.SuccessRehashNeeded) account.PasswordHash = Hasher.HashPassword(account, password);
            account.FailedLogins = 0;
            account.LockedUntil = null;
            account.LastLoginAt = now;
            audit.Record("LoginSucceeded", nameof(UserAccount), account.Id, details: account.UserName);
            await db.SaveChangesAsync(ct);
        }
        return ToSession(account);
    }

    private static readonly string DummyHash = Hasher.HashPassword(new UserAccount(), "not-a-real-password");

    /// <summary>
    /// Entra ID sign-in (spec §69): the token's object id maps to <see cref="UserAccount.EntraObjectId"/>; on first
    /// sign-in an email / UPN match to the employee links the account. Nobody is provisioned automatically.
    /// </summary>
    public async Task<SessionInfo> FindEntraAccountAsync(string? objectId, IEnumerable<string?> emails, CancellationToken ct)
    {
        UserAccount? account = null;
        if (!string.IsNullOrWhiteSpace(objectId))
            account = await db.UserAccounts.Include(a => a.Employee).FirstOrDefaultAsync(a => a.EntraObjectId == objectId, ct);
        if (account is null)
        {
            var candidates = emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!.Trim().ToLowerInvariant()).Distinct().ToList();
            if (candidates.Count > 0)
            {
                account = await db.UserAccounts.Include(a => a.Employee)
                    .FirstOrDefaultAsync(a => a.Employee != null && candidates.Contains(a.Employee.Email.ToLower()), ct);
                if (account is not null && !string.IsNullOrWhiteSpace(objectId) && account.EntraObjectId is null)
                {
                    account.EntraObjectId = objectId;
                    using (actor.AsSystem(ChangeSource.User))
                        audit.Record("EntraLinked", nameof(UserAccount), account.Id, details: account.UserName);
                    await db.SaveChangesAsync(ct);
                }
            }
        }
        if (account is null) throw new AuthenticationFailedException("no_account", "There is no Follow-up Officer account for this Microsoft account.");
        if (!account.IsEnabled || account.Employee is not { IsActive: true }) throw new AuthenticationFailedException("account_disabled", "This account is disabled.");
        return ToSession(account);
    }

    /// <summary>Per-request check: the account still exists, is enabled and its security stamp has not changed.</summary>
    public async Task<SessionInfo?> ValidateAsync(Guid accountId, int securityStamp, CancellationToken ct)
    {
        var account = await db.UserAccounts.AsNoTracking().Include(a => a.Employee).FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null || !account.IsEnabled || account.Employee is not { IsActive: true } || account.SecurityStamp != securityStamp) return null;
        return ToSession(account);
    }

    private static SessionInfo ToSession(UserAccount a) =>
        new(a.Id, a.EmployeeId, a.Employee?.DisplayName ?? a.UserName, a.UserName, a.Roles == AppRole.None ? AppRole.TeamMember : a.Roles, a.SecurityStamp);

    public async Task<MeDto> MeAsync(SessionInfo s, Viewer viewer, CancellationToken ct)
    {
        var e = await db.Employees.AsNoTracking().Where(x => x.Id == s.EmployeeId)
            .Select(x => new { x.DisplayName, x.Email, x.JobTitle, x.DepartmentId, Dept = x.Department != null ? x.Department.Name : null })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Employee");
        var roles = Enum.GetValues<AppRole>().Where(r => r != AppRole.None && s.Roles.HasFlag(r)).Select(r => r.ToString()).ToList();
        return new MeDto(s.EmployeeId, e.DisplayName, e.Email, e.JobTitle, e.DepartmentId, e.Dept, roles, viewer.IsExecutive, viewer.IsAdmin,
            viewer.IsManagement, [.. viewer.TeamIds], [.. viewer.LedTeamIds], s.UserName);
    }

    /// <summary>The headline role for a login (demo user list).</summary>
    public static string PrimaryRole(AppRole roles) =>
        roles.HasFlag(AppRole.Executive) ? "Executive"
        : roles.HasFlag(AppRole.Administrator) ? "Administrator"
        : roles.HasFlag(AppRole.SeniorManager) ? "SeniorManager"
        : roles.HasFlag(AppRole.TeamLeader) ? "TeamLeader"
        : "TeamMember";

    /// <summary>Accounts offered on the demo sign-in screen (only when demo mode is on).</summary>
    public async Task<List<DemoUserDto>> DemoUsersAsync(CancellationToken ct)
    {
        var rows = await db.UserAccounts.AsNoTracking().Where(a => a.IsEnabled && a.PasswordHash != null && a.Employee != null && a.Employee.IsActive)
            .Select(a => new { a.UserName, a.Roles, a.Employee!.DisplayName, a.Employee.JobTitle }).ToListAsync(ct);
        return rows.Select(a => new DemoUserDto(a.UserName, a.DisplayName, a.JobTitle, PrimaryRole(a.Roles)))
            .OrderBy(a => a.Role switch { "Executive" => 0, "Administrator" => 1, "SeniorManager" => 2, "TeamLeader" => 3, _ => 4 })
            .ThenBy(a => a.Name).ToList();
    }
}
