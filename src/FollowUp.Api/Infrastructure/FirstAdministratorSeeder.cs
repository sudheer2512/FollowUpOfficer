using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Core.Platform;
using FollowUp.Core.Services;
using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Api.Infrastructure;

/// <summary>The "Bootstrap" settings. AdminPassword is only needed for the first start: remove it afterwards.</summary>
internal sealed class BootstrapOptions
{
    public const string Section = "Bootstrap";

    public string? AdminUserName { get; set; }
    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }
    public string AdminDisplayName { get; set; } = "Administrator";
}

/// <summary>
/// First administrator of a clean install. Only an administrator can add users, so without demo data nobody could
/// sign in. Does nothing once any administrator exists, even a disabled one: a section left in the settings file must
/// not undo a lockout. Never changes an existing account.
/// </summary>
internal sealed class FirstAdministratorSeeder(ILogger<FirstAdministratorSeeder> logger) : IStartupSeeder
{
    /// <summary>After the demo data, which brings its own administrator.</summary>
    public int Order => 30;

    public async Task SeedAsync(IServiceProvider sp, CancellationToken ct)
    {
        var bootstrap = sp.GetRequiredService<IConfiguration>().GetSection(BootstrapOptions.Section).Get<BootstrapOptions>();
        if (string.IsNullOrEmpty(bootstrap?.AdminPassword)) return;

        var db = sp.GetRequiredService<FollowUpDbContext>();
        if (await db.UserAccounts.AnyAsync(a => (a.Roles & AppRole.Administrator) != 0, ct))
        {
            logger.LogWarning("An administrator already exists, so the Bootstrap settings were ignored. Remove Bootstrap:AdminPassword from the settings file.");
            return;
        }

        // Checked first: a failure after the employee is saved would block the next attempt with a duplicate email.
        if (string.IsNullOrWhiteSpace(bootstrap.AdminUserName) || string.IsNullOrWhiteSpace(bootstrap.AdminEmail))
            throw new ValidationException("bootstrap_incomplete", "Bootstrap needs AdminUserName, AdminEmail and AdminPassword.");
        if (bootstrap.AdminPassword.Length < OrgService.MinPasswordLength)
            throw new ValidationException("password_too_short", $"Bootstrap:AdminPassword needs at least {OrgService.MinPasswordLength} characters.");

        var org = sp.GetRequiredService<OrgService>();
        using (sp.GetRequiredService<ActorContext>().AsSystem(ChangeSource.System))
        {
            var employee = new EmployeeInput("ADMIN", bootstrap.AdminDisplayName, bootstrap.AdminEmail, "Administrator", null, null, null, IsActive: true);
            var admin = await org.SaveEmployeeAsync(null, employee, ct);
            var account = new AccountInput(bootstrap.AdminUserName, AppRole.Administrator | AppRole.TeamMember, Enabled: true, bootstrap.AdminPassword);
            await org.SetAccountAsync(admin.Id, account, ct);
        }
        logger.LogWarning("Created the first administrator {UserName}. Remove Bootstrap:AdminPassword from the settings file.", bootstrap.AdminUserName);
    }
}
