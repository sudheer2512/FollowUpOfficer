using FollowUp.Api.Infrastructure;
using FollowUp.Core.Contracts;
using FollowUp.Core.Services;

namespace FollowUp.Api.Endpoints;

/// <summary>/api/admin — organisation, calendars, escalation policies, settings, monitor, jobs, sync, notifications, errors, audit.</summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this RouteGroupBuilder api)
    {
        // The monitor (and its read-only details) is also open to executives; everything else is Administrator only.
        var monitor = api.MapGroup("/admin").WithTags("Admin").RequireAuthorization(Policies.ExecutiveOrAdmin);
        monitor.MapGet("/monitor", (AdminService a, CancellationToken ct) => a.OverviewAsync(ct));
        monitor.MapGet("/jobs", (AdminService a, CancellationToken ct) => a.JobsAsync(ct));
        monitor.MapGet("/jobs/{key}/executions", (string key, AdminService a, CancellationToken ct) => a.ExecutionsAsync(key, ct));
        monitor.MapGet("/errors", (AdminService a, CancellationToken ct) => a.ErrorsAsync(ct));

        var g = api.MapGroup("/admin").WithTags("Admin").RequireAuthorization(Policies.Administrator);

        g.MapGet("/departments", (OrgService o, CancellationToken ct) => o.DepartmentsAsync(ct));
        g.MapGet("/departments/{id:guid}", (Guid id, OrgService o, CancellationToken ct) => o.DepartmentAsync(id, ct));
        g.MapPost("/departments", (DepartmentInput r, OrgService o, CancellationToken ct) => o.SaveDepartmentAsync(null, r, ct));
        g.MapPut("/departments/{id:guid}", (Guid id, DepartmentInput r, OrgService o, CancellationToken ct) => o.SaveDepartmentAsync(id, r, ct));

        g.MapGet("/teams", (OrgService o, CancellationToken ct) => o.TeamsAsync(ct));
        g.MapGet("/teams/{id:guid}", (Guid id, OrgService o, CancellationToken ct) => o.TeamAsync(id, ct));
        g.MapPost("/teams", (TeamInput r, OrgService o, CancellationToken ct) => o.SaveTeamAsync(null, r, ct));
        g.MapPut("/teams/{id:guid}", (Guid id, TeamInput r, OrgService o, CancellationToken ct) => o.SaveTeamAsync(id, r, ct));

        g.MapGet("/employees", (OrgService o, CancellationToken ct) => o.EmployeesAsync(ct));
        g.MapGet("/employees/{id:guid}", (Guid id, OrgService o, CancellationToken ct) => o.EmployeeAsync(id, ct));
        g.MapPost("/employees", (EmployeeInput r, OrgService o, CancellationToken ct) => o.SaveEmployeeAsync(null, r, ct));
        g.MapPut("/employees/{id:guid}", (Guid id, EmployeeInput r, OrgService o, CancellationToken ct) => o.SaveEmployeeAsync(id, r, ct));
        g.MapPut("/employees/{id:guid}/account", (Guid id, AccountInput r, OrgService o, CancellationToken ct) => o.SetAccountAsync(id, r, ct));

        g.MapGet("/calendars", (OrgService o, CancellationToken ct) => o.CalendarsAsync(ct));
        g.MapGet("/calendars/{id:guid}", (Guid id, OrgService o, CancellationToken ct) => o.CalendarAsync(id, ct));
        g.MapPost("/calendars", (CalendarInput r, OrgService o, CancellationToken ct) => o.SaveCalendarAsync(null, r, ct));
        g.MapPut("/calendars/{id:guid}", (Guid id, CalendarInput r, OrgService o, CancellationToken ct) => o.SaveCalendarAsync(id, r, ct));
        g.MapPost("/calendars/{id:guid}/holidays", (Guid id, HolidayInput r, OrgService o, CancellationToken ct) => o.AddHolidayAsync(id, r, ct));
        g.MapDelete("/calendars/{id:guid}/holidays/{holidayId:guid}", (Guid id, Guid holidayId, OrgService o, CancellationToken ct) =>
            o.RemoveHolidayAsync(id, holidayId, ct));

        g.MapGet("/escalation-policies", (OrgService o, CancellationToken ct) => o.PoliciesAsync(ct));
        g.MapGet("/escalation-policies/{id:guid}", (Guid id, OrgService o, CancellationToken ct) => o.PolicyAsync(id, ct));
        g.MapPost("/escalation-policies", (EscalationPolicyInput r, OrgService o, CancellationToken ct) => o.SavePolicyAsync(null, r, ct));
        g.MapPut("/escalation-policies/{id:guid}", (Guid id, EscalationPolicyInput r, OrgService o, CancellationToken ct) => o.SavePolicyAsync(id, r, ct));

        g.MapGet("/settings", (OrgService o, CancellationToken ct) => o.SettingsAsync(ct));
        g.MapPut("/settings", (RuntimeSettings r, OrgService o, CancellationToken ct) => o.SaveSettingsAsync(r, ct));

        g.MapPost("/jobs/{key}/run", async (string key, AdminService a, CancellationToken ct) =>
        {
            await a.RunJobNowAsync(key, ct);
            return Results.NoContent();
        });
        g.MapPut("/jobs/{key}", (string key, EnabledRequest r, AdminService a, CancellationToken ct) => a.SetJobEnabledAsync(key, r.Enabled, ct));

        g.MapPost("/checkpoints/{id:guid}/reset", async (Guid id, ResetCheckpointRequest r, AdminService a, CancellationToken ct) =>
        {
            await a.ResetCheckpointAsync(id, r.FromScratch, ct);
            return Results.NoContent();
        });
        g.MapPut("/checkpoints/{id:guid}", async (Guid id, EnabledRequest r, AdminService a, CancellationToken ct) =>
        {
            await a.SetCheckpointEnabledAsync(id, r.Enabled, ct);
            return Results.NoContent();
        });
        g.MapPost("/sync/run-now", async (AdminService a, CancellationToken ct) =>
        {
            await a.SyncNowAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/notifications", (string? status, AdminService a, CancellationToken ct) => a.NotificationsAsync(status, ct));
        g.MapPost("/notifications/retry", async (IdsRequest r, AdminService a, CancellationToken ct) =>
            new RetryResult(await a.RetryNotificationsAsync(r.Ids ?? [], ct)));
        g.MapGet("/messages/failed", (AdminService a, CancellationToken ct) => a.FailedMessagesAsync(ct));

        g.MapGet("/audit", (Guid? taskId, string? entityType, string? action, DateOnly? from, DateOnly? to, int? page, int? pageSize, TaskQueryService q,
                CancellationToken ct) => q.AuditAsync(new AuditQuery(taskId, entityType, action, from, to, page ?? 1, pageSize ?? 100), ct));
        g.MapGet("/audit/verify/{taskId:guid}", (Guid taskId, AdminService a, CancellationToken ct) => a.VerifyAuditAsync(taskId, ct));
    }
}
