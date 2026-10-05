using FollowUp.Core.Contracts;
using FollowUp.Core.Services;
using FollowUp.Domain;

namespace FollowUp.Api.Endpoints;

/// <summary>/api/messages — imported emails and Teams messages, the low-confidence review queue and manual decisions.</summary>
public static class MessageEndpoints
{
    public static void MapMessageEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/messages").WithTags("Messages");

        g.MapGet("", (string? text, CommunicationChannel? channel, MessageProcessingStatus? status, bool? unlinked, DateOnly? from, DateOnly? to, int? page,
                int? pageSize, CommunicationService c, CancellationToken ct) =>
            c.ListAsync(new MessageFilter(text, channel, status, unlinked, from, to, page ?? 1, pageSize ?? 50), ct));
        g.MapGet("/review", (CommunicationService c, CancellationToken ct) => c.ReviewQueueAsync(ct));
        g.MapGet("/{id:guid}", (Guid id, CommunicationService c, CancellationToken ct) => c.GetAsync(id, ct));
        g.MapPost("/links/{linkId:guid}/confirm", async (Guid linkId, CommunicationService c, CancellationToken ct) =>
        {
            await c.DecideLinkAsync(linkId, true, ct);
            return Results.NoContent();
        });
        g.MapPost("/links/{linkId:guid}/reject", async (Guid linkId, CommunicationService c, CancellationToken ct) =>
        {
            await c.DecideLinkAsync(linkId, false, ct);
            return Results.NoContent();
        });
        g.MapPost("/{id:guid}/reprocess", async (Guid id, AdminService admin, CancellationToken ct) =>
        {
            await admin.ReprocessMessageAsync(id, ct);
            return Results.NoContent();
        });
    }
}
