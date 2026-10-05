namespace FollowUp.Domain;

/// <summary>Identifier helpers. All primary keys are GUIDs (spec §1, §75).</summary>
public static class Ids
{
    private static long _counter = DateTime.UtcNow.Ticks;

    /// <summary>
    /// SQL Server-friendly sequential GUID: the timestamp counter is written into the last six bytes,
    /// which SQL Server compares first, so clustered inserts stay append-mostly.
    /// </summary>
    public static Guid New()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var counter = BitConverter.GetBytes(Interlocked.Increment(ref _counter));
        if (!BitConverter.IsLittleEndian) Array.Reverse(counter);
        bytes[08] = counter[1];
        bytes[09] = counter[0];
        bytes[10] = counter[7];
        bytes[11] = counter[6];
        bytes[12] = counter[5];
        bytes[13] = counter[4];
        bytes[14] = counter[3];
        bytes[15] = counter[2];
        return new Guid(bytes);
    }

    /// <summary>Deterministic name-based GUID (RFC 4122 v5 layout) — used where an id must be reproducible.</summary>
    public static Guid Deterministic(string scope, string name)
    {
        var nsBytes = System.Text.Encoding.UTF8.GetBytes("followup:" + scope + ":");
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var hash = System.Security.Cryptography.SHA1.HashData([.. nsBytes, .. nameBytes]);
        var g = new byte[16];
        Array.Copy(hash, g, 16);
        g[6] = (byte)((g[6] & 0x0F) | 0x50);
        g[8] = (byte)((g[8] & 0x3F) | 0x80);
        return new Guid(g);
    }
}

/// <summary>Time source. Every rule and service takes time from here so tests and the demo seeder can pin it.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>A clock that can be pinned (seeding, tests) and otherwise follows real time with an optional offset.</summary>
public sealed class MutableClock : IClock
{
    private DateTime? _pinned;
    private TimeSpan _offset;
    private readonly object _gate = new();

    public DateTime UtcNow
    {
        get { lock (_gate) return _pinned ?? DateTime.UtcNow + _offset; }
    }

    public void Pin(DateTime utc) { lock (_gate) _pinned = DateTime.SpecifyKind(utc, DateTimeKind.Utc); }
    public void Advance(TimeSpan by) { lock (_gate) { if (_pinned is { } p) _pinned = p + by; else _offset += by; } }
    public void Release() { lock (_gate) _pinned = null; }
    public void SetOffset(TimeSpan offset) { lock (_gate) _offset = offset; }
    public TimeSpan Offset { get { lock (_gate) return _offset; } }
    public bool IsPinned { get { lock (_gate) return _pinned is not null; } }
}

public abstract class Entity
{
    public Guid Id { get; set; } = Ids.New();
}

/// <summary>Entities stamped with creation / modification times by the DbContext.</summary>
public interface IStamped
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}

/// <summary>Domain rule violation — surfaces to API callers as 400/409 with a stable code.</summary>
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
