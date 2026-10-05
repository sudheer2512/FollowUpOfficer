using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Core.Services;

/// <summary>In-app notification inbox for the signed-in user (spec §19, §20 — the in-app channel).</summary>
public sealed class InboxService(FollowUpDbContext db, ActorContext actor, IClock clock)
{
    private Guid Me => actor.EmployeeId ?? throw new ForbiddenException("Sign in to see your notifications.");

    private IQueryable<Notification> Mine(Guid me) =>
        db.Notifications.Where(n => n.RecipientEmployeeId == me && n.Channel == NotificationChannel.InApp && n.Status == NotificationStatus.Sent);

    public async Task<List<InboxItem>> ListAsync(CancellationToken ct, int take = 50)
    {
        var me = Me;
        var rows = await Mine(me).AsNoTracking().OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(take, 1, 200))
            .Select(n => new { n.Id, n.Category, n.Subject, n.Body, n.TaskId, n.CreatedAt, n.ReadAt,
                Number = db.Tasks.Where(t => t.Id == n.TaskId).Select(t => t.Number).FirstOrDefault() })
            .ToListAsync(ct);
        return rows.Select(n => new InboxItem(n.Id, n.Category, n.Subject, n.Body, n.TaskId, n.Number, n.CreatedAt, n.ReadAt)).ToList();
    }

    public Task<int> UnreadCountAsync(CancellationToken ct) => Mine(Me).CountAsync(n => n.ReadAt == null, ct);

    public async Task MarkReadAsync(Guid id, CancellationToken ct)
    {
        var me = Me;
        var n = await Mine(me).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Notification");
        if (n.ReadAt is not null) return;
        n.ReadAt = clock.UtcNow;
        UiEvents.Publish(db, clock, UiEvents.InboxChanged, n.TaskId, me);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> MarkAllReadAsync(CancellationToken ct)
    {
        var me = Me;
        var now = clock.UtcNow;
        var count = await Mine(me).Where(n => n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        if (count > 0)
        {
            UiEvents.Publish(db, clock, UiEvents.InboxChanged, null, me);
            await db.SaveChangesAsync(ct);
        }
        return count;
    }
}
