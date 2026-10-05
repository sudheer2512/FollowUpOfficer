using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FollowUp.Core.Data;

/// <summary>JSON options for value objects stored in text columns (enums as strings, camelCase).</summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

/// <summary>Stores a value object / collection as JSON text.</summary>
public sealed class JsonValueConverter<T>() : ValueConverter<T, string>(
    v => JsonSerializer.Serialize(v, JsonDefaults.Options),
    s => string.IsNullOrEmpty(s) ? default! : JsonSerializer.Deserialize<T>(s, JsonDefaults.Options)!);

/// <summary>Compares JSON-stored values by their serialised form so EF detects in-place changes.</summary>
public sealed class JsonValueComparer<T>() : ValueComparer<T>(
    (a, b) => JsonSerializer.Serialize(a, JsonDefaults.Options) == JsonSerializer.Serialize(b, JsonDefaults.Options),
    v => v == null ? 0 : JsonSerializer.Serialize(v, JsonDefaults.Options).GetHashCode(),
    v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonDefaults.Options), JsonDefaults.Options)!);

/// <summary>All DateTime values are UTC; SQL Server / SQLite return Unspecified kind, so re-tag on read.</summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v.HasValue && v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : v,
    v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
