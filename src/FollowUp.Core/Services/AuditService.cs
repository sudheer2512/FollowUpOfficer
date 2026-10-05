using System.Security.Cryptography;
using System.Text;
using FollowUp.Core.Common;
using FollowUp.Core.Data;
using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Core.Services;

/// <summary>
/// Immutable audit trail (spec §68). Entries are append-only (enforced by the DbContext and a SQL trigger) and
/// hash-chained per task/entity so tampering is detectable. Entries are added to the caller's unit of work.
/// </summary>
public sealed class AuditService(FollowUpDbContext db, ActorContext actor, IClock clock)
{
    private readonly Dictionary<string, string?> _lastHash = [];
    private DateTime _lastAt;

    public void Record(string action, string entityType, Guid? entityId, Guid? taskId = null, string? field = null,
        string? oldValue = null, string? newValue = null, string? details = null)
    {
        var chainKey = (taskId ?? entityId)?.ToString("N") ?? entityType;
        if (!_lastHash.TryGetValue(chainKey, out var prev))
        {
            prev = db.AuditLogs.Local.Where(a => (a.TaskId ?? a.EntityId)?.ToString("N") == chainKey).OrderBy(a => a.At).LastOrDefault()?.Hash
                ?? (taskId is { } t
                    ? db.AuditLogs.AsNoTracking().Where(a => a.TaskId == t).OrderByDescending(a => a.At).Select(a => a.Hash).FirstOrDefault()
                    : entityId is { } e
                        ? db.AuditLogs.AsNoTracking().Where(a => a.EntityId == e && a.TaskId == null).OrderByDescending(a => a.At).Select(a => a.Hash).FirstOrDefault()
                        : null);
        }

        // Strictly increasing timestamps keep the chain order unambiguous (several entries per unit of work).
        var at = clock.UtcNow;
        if (at <= _lastAt) at = _lastAt.AddTicks(1);
        _lastAt = at;

        var entry = new AuditLog
        {
            At = at,
            ActorId = actor.EmployeeId,
            ActorName = actor.Name,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            TaskId = taskId,
            Field = field,
            OldValue = Trim(oldValue),
            NewValue = Trim(newValue),
            Details = Trim(details),
            Source = actor.Source,
            PrevHash = prev,
        };
        entry.Hash = ComputeHash(entry);
        _lastHash[chainKey] = entry.Hash;
        db.AuditLogs.Add(entry);
    }

    /// <summary>Records a field change only when the value actually changed.</summary>
    public void Change<T>(string action, WorkTask task, string field, T oldValue, T newValue, string? details = null)
    {
        if (EqualityComparer<T>.Default.Equals(oldValue, newValue)) return;
        Record(action, nameof(WorkTask), task.Id, task.Id, field, Format(oldValue), Format(newValue), details);
    }

    public static string? Format<T>(T value) => value switch
    {
        null => null,
        DateOnly d => d.ToString("yyyy-MM-dd"),
        DateTime dt => dt.ToString("u"),
        _ => value.ToString(),
    };

    public static string ComputeHash(AuditLog e)
    {
        var payload = string.Join("|", e.PrevHash, e.At.ToString("O"), e.ActorId, e.Action, e.EntityType, e.EntityId, e.TaskId,
            e.Field, e.OldValue, e.NewValue, e.Details, e.Source);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>Verifies the hash chain for one task; returns the first broken entry or null.</summary>
    public async Task<AuditLog?> VerifyTaskChainAsync(Guid taskId, CancellationToken ct)
    {
        var entries = await db.AuditLogs.AsNoTracking().Where(a => a.TaskId == taskId).OrderBy(a => a.At).ToListAsync(ct);
        string? prev = null;
        foreach (var e in entries)
        {
            if (e.PrevHash != prev || ComputeHash(e) != e.Hash) return e;
            prev = e.Hash;
        }
        return null;
    }

    private static string? Trim(string? s) => s is null ? null : s.Length <= 2000 ? s : s[..1997] + "...";
}
