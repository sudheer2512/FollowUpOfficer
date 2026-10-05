using FollowUp.Core.Data;
using FollowUp.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Api.Infrastructure;

/// <summary>Real-time hub (/hubs/events). Clients only listen for "event"; they refetch what they show.</summary>
[Authorize]
public sealed class EventsHub : Hub;

/// <summary>The SignalR user id is the employee id, so per-person events (inbox) reach only that person.</summary>
public sealed class EmployeeUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst(FollowUpClaims.Employee)?.Value;
}

/// <summary>Event pushed to clients: { topic, taskId?, payload? }.</summary>
public sealed record RealtimeEvent(string Topic, Guid? TaskId, string? Payload);

/// <summary>
/// Tails the UiEvents outbox by its monotonic id (starting from the newest row at start-up) once a second and
/// broadcasts new rows to connected clients. Works for changes made by the separate Worker process too.
/// Duplicate events within a poll are coalesced. Disable with Realtime:Enabled=false.
/// </summary>
public sealed class UiEventRelay(IServiceScopeFactory scopes, IHubContext<EventsHub> hub, IConfiguration config, ILogger<UiEventRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Realtime:Enabled", true)) return;
        var interval = TimeSpan.FromMilliseconds(Math.Max(250, config.GetValue("Realtime:PollMilliseconds", 1000)));
        long? cursor = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
                if (cursor is null) cursor = await db.UiEvents.MaxAsync(e => (long?)e.Id, stoppingToken) ?? 0;
                else
                {
                    var from = cursor.Value;
                    var batch = await db.UiEvents.AsNoTracking().Where(e => e.Id > from).OrderBy(e => e.Id).Take(500).ToListAsync(stoppingToken);
                    if (batch.Count > 0)
                    {
                        cursor = batch[^1].Id;
                        foreach (var e in batch.DistinctBy(e => (e.Topic, e.TaskId, e.AudienceId, e.Payload)))
                        {
                            var message = new RealtimeEvent(e.Topic, e.TaskId, e.Payload);
                            if (e.AudienceId is { } audience && e.Topic == UiEvents.InboxChanged)
                                await hub.Clients.User(audience.ToString()).SendAsync("event", message, stoppingToken);
                            else
                                await hub.Clients.All.SendAsync("event", message, stoppingToken);
                        }
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Real-time relay poll failed; retrying");
            }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
