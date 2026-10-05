using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Services;
using FollowUp.Domain;

namespace FollowUp.Api.Endpoints;

/// <summary>/api/recurring — recurring task definitions, preview and occurrence history (spec §11–§15).</summary>
public static class RecurringEndpoints
{
    public static void MapRecurringEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/recurring").WithTags("Recurring");

        g.MapGet("", (bool? includeEnded, RecurringTaskService r, CancellationToken ct) => r.ListAsync(includeEnded ?? false, ct));
        g.MapGet("/{id:guid}", (Guid id, RecurringTaskService r, CancellationToken ct) => r.GetAsync(id, ct));
        g.MapPost("", async (RecurringDefinitionInput input, RecurringTaskService r, CancellationToken ct) =>
            await r.GetAsync((await r.CreateAsync(input, ct)).Id, ct));
        g.MapPut("/{id:guid}", async (Guid id, RecurringUpdateRequest input, RecurringTaskService r, CancellationToken ct) =>
            await r.GetAsync((await r.UpdateAsync(id, input, ct)).Id, ct));
        g.MapPost("/{id:guid}/pause", async (Guid id, RecurringPauseRequest input, RecurringTaskService r, CancellationToken ct) =>
            await r.GetAsync((await r.PauseAsync(id, input, ct)).Id, ct));
        g.MapPost("/{id:guid}/resume", async (Guid id, RecurringTaskService r, CancellationToken ct) =>
            await r.GetAsync((await r.ResumeAsync(id, ct)).Id, ct));
        g.MapPost("/{id:guid}/end", async (Guid id, ReasonRequest input, RecurringTaskService r, CancellationToken ct) =>
            await r.GetAsync((await r.EndAsync(id, input.Reason, ct)).Id, ct));
        g.MapPost("/{id:guid}/skip", async (Guid id, SkipOccurrenceRequest input, RecurringTaskService r, CancellationToken ct) =>
        {
            var instance = await r.SkipAsync(id, input, ct);
            var rows = await r.InstancesAsync(instance.DefinitionId, null, instance.NominalDate, instance.NominalDate, false, ct);
            return rows.FirstOrDefault(x => x.Id == instance.Id) ?? throw new NotFoundException("Occurrence");
        });
        g.MapPost("/preview", (PreviewRequest input, RecurringTaskService r, CancellationToken ct) => r.PreviewAsync(input, ct));
        g.MapGet("/instances", (Guid? definitionId, string? outcome, DateOnly? from, DateOnly? to, bool? missedOnly, RecurringTaskService r, CancellationToken ct) =>
        {
            var outcomes = Http.Enums<OccurrenceOutcome>(outcome);
            return r.InstancesAsync(definitionId, outcomes.Count == 0 ? null : outcomes, from, to, missedOnly ?? false, ct);
        });
    }
}
