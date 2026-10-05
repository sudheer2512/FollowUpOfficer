using FollowUp.Api.Infrastructure;
using FollowUp.Core.Contracts;
using FollowUp.Core.Services;
using FollowUp.Domain;

namespace FollowUp.Api.Endpoints;

/// <summary>Executive views: workbench, dashboards, attention queue, summary, digest, weekly review, Ask, commitments, reports, inbox.</summary>
public static class ExecutiveEndpoints
{
    public static void MapExecutiveEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Executive");

        g.MapGet("/workbench", (DashboardService d, CancellationToken ct) => d.CeoWorkbenchAsync(ct));
        g.MapGet("/dashboard/me", (DashboardService d, CancellationToken ct) => d.MyDashboardAsync(ct));
        g.MapGet("/dashboard/teams", (DashboardService d, CancellationToken ct) => d.TeamsAsync(ct));
        g.MapGet("/dashboard/team/{teamId:guid}", (Guid teamId, DashboardService d, CancellationToken ct) => d.TeamDashboardAsync(teamId, ct));

        g.MapGet("/attention", (string? status, AttentionService a, CancellationToken ct) => a.ListAsync(status, ct));
        g.MapPost("/attention/{id:guid}/decision", (Guid id, AttentionDecision r, AttentionService a, CancellationToken ct) => a.DecideAsync(id, r, ct));

        g.MapGet("/summary", (DigestService d, CancellationToken ct) => d.ExecutiveSummaryAsync(ct));
        g.MapGet("/digest/preview", (DigestService d, CancellationToken ct) => d.DigestAsync(ct)).RequireAuthorization(Policies.Manager);
        g.MapPost("/digest/send", async (DigestService d, CancellationToken ct) => new QueuedResult(await d.SendDailyDigestAsync(ct)))
            .RequireAuthorization(Policies.ExecutiveOrAdmin);
        g.MapGet("/review/weekly", (DateOnly? weekStart, DigestService d, CancellationToken ct) => d.WeeklyReviewAsync(weekStart, ct))
            .RequireAuthorization(Policies.Manager);
        g.MapPost("/review/weekly/send", async (DateOnly? weekStart, DigestService d, CancellationToken ct) =>
            new QueuedResult(await d.SendWeeklyReviewAsync(weekStart, ct))).RequireAuthorization(Policies.ExecutiveOrAdmin);

        g.MapPost("/ask", (AskRequest r, AskService ask, CancellationToken ct) => ask.AskAsync(r.Question, ct));

        g.MapGet("/commitments", (CommitmentStatus? status, bool? toMe, Guid? employeeId, DateOnly? from, DateOnly? to, CommitmentService c, CancellationToken ct) =>
            c.ListAsync(status, toMe ?? false, employeeId, from, to, ct));
        g.MapGet("/reports", (DateOnly? from, DateOnly? to, Guid? departmentId, Guid? teamId, ReportService r, CancellationToken ct) =>
            r.ReportAsync(new ReportRequest(from, to, departmentId, teamId), ct));

        g.MapGet("/inbox", (InboxService i, CancellationToken ct) => i.ListAsync(ct));
        g.MapGet("/inbox/unread-count", async (InboxService i, CancellationToken ct) => new CountResult(await i.UnreadCountAsync(ct)));
        g.MapPost("/inbox/{id:guid}/read", async (Guid id, InboxService i, CancellationToken ct) =>
        {
            await i.MarkReadAsync(id, ct);
            return Results.NoContent();
        });
        g.MapPost("/inbox/read-all", async (InboxService i, CancellationToken ct) =>
        {
            await i.MarkAllReadAsync(ct);
            return Results.NoContent();
        });
    }
}
