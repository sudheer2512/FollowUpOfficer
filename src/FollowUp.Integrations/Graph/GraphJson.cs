using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FollowUp.Integrations.Graph;

/// <summary>JSON conventions shared by the Graph client and the Graph emulator (camelCase, nulls omitted, UTC "Z" timestamps).</summary>
internal static class GraphJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }

    /// <summary>OData literal for a UTC timestamp, e.g. 2026-10-05T09:30:00.000Z.</summary>
    public static string FormatDate(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value,
        };
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>Parses an OData / ISO-8601 timestamp as UTC (values without an offset are taken as UTC).</summary>
    public static bool TryParseDate(string? text, out DateTime utc)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            utc = parsed.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }

    /// <summary>Escapes a string literal for an OData $filter ('' doubling).</summary>
    public static string ODataString(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Escapes one URL path segment (ids may contain ':' '@' '=').</summary>
    public static string Segment(string value) => Uri.EscapeDataString(value);

    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString();
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
                throw new JsonException($"Invalid timestamp '{text}'.");
            return value;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
