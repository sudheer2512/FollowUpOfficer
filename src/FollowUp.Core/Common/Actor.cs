using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Core.Common;

/// <summary>
/// Who is acting in the current scope: a signed-in user (API) or the system (background jobs).
/// Services use it for authorisation (task-level visibility) and audit attribution.
/// </summary>
public sealed class ActorContext
{
    public Guid? EmployeeId { get; private set; }
    public string Name { get; private set; } = "Follow-up Officer";
    public Viewer Viewer { get; private set; } = Viewer.System;
    public ChangeSource Source { get; private set; } = ChangeSource.System;
    public bool IsSystem => EmployeeId is null;

    public void SetUser(Guid employeeId, string name, Viewer viewer)
    {
        EmployeeId = employeeId;
        Name = name;
        Viewer = viewer;
        Source = ChangeSource.User;
    }

    public void SetSystem(ChangeSource source = ChangeSource.System, string name = "Follow-up Officer")
    {
        EmployeeId = null;
        Name = name;
        Viewer = Viewer.System;
        Source = source;
    }

    /// <summary>Temporarily act as the system (e.g. AI-suggested changes applied on a user's behalf).</summary>
    public IDisposable AsSystem(ChangeSource source)
    {
        var (id, name, viewer, src) = (EmployeeId, Name, Viewer, Source);
        SetSystem(source);
        return new Restore(() => { EmployeeId = id; Name = name; Viewer = viewer; Source = src; });
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>Builds a <see cref="Viewer"/> (roles + team / department scope) for an employee.</summary>
public static class ViewerFactory
{
    public static async Task<Viewer> BuildAsync(FollowUpDbContext db, Guid employeeId, AppRole roles, CancellationToken ct)
    {
        var teamIds = await db.TeamMembers.Where(m => m.EmployeeId == employeeId).Select(m => m.TeamId).ToListAsync(ct);
        var ledTeams = await db.Teams.Where(t => t.LeaderId == employeeId || t.DeputyLeaderId == employeeId).Select(t => t.Id).ToListAsync(ct);
        var managedDepartments = await db.Departments.Where(d => d.HeadId == employeeId).Select(d => d.Id).ToListAsync(ct);
        if (roles.HasFlag(AppRole.SeniorManager))
        {
            var own = await db.Employees.Where(e => e.Id == employeeId).Select(e => e.DepartmentId).FirstOrDefaultAsync(ct);
            if (own is { } d) managedDepartments.Add(d);
        }
        return new Viewer(employeeId, roles, teamIds.Concat(ledTeams).ToHashSet(), ledTeams.ToHashSet(), managedDepartments.ToHashSet());
    }
}
