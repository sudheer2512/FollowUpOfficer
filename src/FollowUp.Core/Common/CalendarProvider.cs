using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Core.Common;

/// <summary>
/// Supplies the organisation's business calendar (time zone, weekends, holidays). Cached briefly; refreshed
/// when holidays change. Deadline and recurrence arithmetic always goes through it (spec §73).
/// </summary>
public sealed class CalendarProvider(IServiceScopeFactory scopes, IClock clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<Guid, BusinessCalendar> _byId = [];
    private BusinessCalendar? _default;
    private DateTime _loadedAt;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public async Task<BusinessCalendar> GetDefaultAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _default ?? BusinessCalendar.Default;
    }

    public async Task<BusinessCalendar> GetAsync(Guid? calendarId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        if (calendarId is { } id && _byId.TryGetValue(id, out var cal)) return cal;
        return _default ?? BusinessCalendar.Default;
    }

    public void Invalidate() => _loadedAt = default;

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_default is not null && clock.UtcNow - _loadedAt < Ttl) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_default is not null && clock.UtcNow - _loadedAt < Ttl) return;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
            var calendars = await db.WorkCalendars.AsNoTracking().Include(c => c.Holidays).ToListAsync(ct);
            _byId = calendars.ToDictionary(c => c.Id, BusinessCalendar.From);
            var def = calendars.FirstOrDefault(c => c.IsDefault) ?? calendars.FirstOrDefault();
            _default = def is null ? BusinessCalendar.Default : _byId[def.Id];
            _loadedAt = clock.UtcNow;
        }
        finally { _gate.Release(); }
    }
}
