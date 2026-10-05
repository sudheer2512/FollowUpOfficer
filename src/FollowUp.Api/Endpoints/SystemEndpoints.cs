using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Core.Services;
using FollowUp.Domain;
using Microsoft.Extensions.Options;

namespace FollowUp.Api.Endpoints;

/// <summary>Health, system info, lookups, global search and saved views.</summary>
public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/health", async (FollowUpDbContext db, IConfiguration config, IOptions<JobEngineOptions> jobs, IClock clock, CancellationToken ct) =>
        {
            string database;
            try { database = await db.Database.CanConnectAsync(ct) ? "ok" : "unavailable"; }
            catch (Exception) { database = "unavailable"; }
            var dto = new HealthDto(database == "ok" ? "ok" : "degraded", database, config["Microsoft365:Mode"] is { Length: > 0 } m ? m : "Disabled",
                jobs.Value.Enabled, clock.UtcNow);
            return Results.Json(dto, statusCode: database == "ok" ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous().WithTags("System");

        api.MapGet("/system/info", (AdminService admin, CancellationToken ct) => admin.SystemInfoAsync(ct)).WithTags("System");
        api.MapGet("/lookups", (TaskQueryService q, CancellationToken ct) => q.LookupsAsync(ct)).WithTags("System");
        api.MapGet("/search", (string? q, TaskQueryService queries, CancellationToken ct) => queries.SearchAsync(q, ct)).WithTags("System");

        var views = api.MapGroup("/views").WithTags("Views");
        views.MapGet("", (PlanningService p, CancellationToken ct) => p.ViewsAsync(ct));
        views.MapPost("", (SavedViewInput r, PlanningService p, CancellationToken ct) => p.SaveViewAsync(null, r, ct));
        views.MapPut("/{id:guid}", (Guid id, SavedViewInput r, PlanningService p, CancellationToken ct) => p.SaveViewAsync(id, r, ct));
        views.MapDelete("/{id:guid}", async (Guid id, PlanningService p, CancellationToken ct) =>
        {
            await p.DeleteViewAsync(id, ct);
            return Results.NoContent();
        });
    }
}
