using FollowUp.Core.Contracts;
using FollowUp.Core.Services;

namespace FollowUp.Api.Endpoints;

/// <summary>/api/projects, /api/meetings and /api/templates (spec §46, §47, §51).</summary>
public static class PlanningEndpoints
{
    public static void MapPlanningEndpoints(this RouteGroupBuilder api)
    {
        var projects = api.MapGroup("/projects").WithTags("Projects");
        projects.MapGet("", (DashboardService d, CancellationToken ct) => d.ProjectsAsync(ct));
        projects.MapGet("/{id:guid}", (Guid id, DashboardService d, CancellationToken ct) => d.ProjectDetailAsync(id, ct));
        projects.MapPost("", (ProjectInput r, PlanningService p, CancellationToken ct) => p.SaveProjectAsync(null, r, ct));
        projects.MapPut("/{id:guid}", (Guid id, ProjectInput r, PlanningService p, CancellationToken ct) => p.SaveProjectAsync(id, r, ct));

        var meetings = api.MapGroup("/meetings").WithTags("Meetings");
        meetings.MapGet("", (PlanningService p, CancellationToken ct) => p.MeetingsAsync(ct));
        meetings.MapGet("/{id:guid}", (Guid id, PlanningService p, CancellationToken ct) => p.MeetingAsync(id, ct));
        meetings.MapPost("", (MeetingInput r, PlanningService p, CancellationToken ct) => p.SaveMeetingAsync(null, r, ct));
        meetings.MapPut("/{id:guid}", (Guid id, MeetingInput r, PlanningService p, CancellationToken ct) => p.SaveMeetingAsync(id, r, ct));
        meetings.MapPost("/{id:guid}/create-tasks", (Guid id, CreateActionTasksRequest r, PlanningService p, CancellationToken ct) =>
            p.CreateTasksFromActionItemsAsync(id, r, ct));

        var templates = api.MapGroup("/templates").WithTags("Templates");
        templates.MapGet("", (PlanningService p, CancellationToken ct) => p.TemplatesAsync(ct));
        templates.MapGet("/{id:guid}", (Guid id, PlanningService p, CancellationToken ct) => p.TemplateAsync(id, ct));
        templates.MapPost("", (TemplateInput r, PlanningService p, CancellationToken ct) => p.SaveTemplateAsync(null, r, ct));
        templates.MapPut("/{id:guid}", (Guid id, TemplateInput r, PlanningService p, CancellationToken ct) => p.SaveTemplateAsync(id, r, ct));
        templates.MapPost("/{id:guid}/instantiate", async (Guid id, InstantiateTemplateRequest r, PlanningService p, TaskQueryService q, CancellationToken ct) =>
            await q.GetDetailAsync((await p.InstantiateAsync(id, r, ct)).Id, ct));
    }
}
