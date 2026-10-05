using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FollowUp.AI.Llm;

/// <summary>
/// Helpers to build compact JSON schemas (every property required, no additional properties — the subset accepted by
/// both Anthropic structured outputs and OpenAI strict json_schema) and to read LLM answers defensively.
/// </summary>
internal static class LlmJson
{
    // ───────────────────────────── schema building ─────────────────────────────

    public static JsonNode Str(string description) => new JsonObject { ["type"] = "string", ["description"] = description };

    public static JsonNode Num(string description) => new JsonObject { ["type"] = "number", ["description"] = description };

    public static JsonNode Int(string description) => new JsonObject { ["type"] = "integer", ["description"] = description };

    public static JsonNode Bool(string description) => new JsonObject { ["type"] = "boolean", ["description"] = description };

    public static JsonNode Enum(IEnumerable<string> values, string description) => new JsonObject
    {
        ["type"] = "string",
        ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v))]),
        ["description"] = description,
    };

    public static JsonNode Arr(JsonNode items, string description) => new JsonObject { ["type"] = "array", ["items"] = items, ["description"] = description };

    public static JsonObject Obj(params (string Name, JsonNode Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties) props[name] = schema;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray([.. properties.Select(p => (JsonNode)JsonValue.Create(p.Name))]),
            ["additionalProperties"] = false,
        };
    }

    public static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node);

    // ───────────────────────────── answer parsing ─────────────────────────────

    /// <summary>Parses a JSON object from model text (tolerating a ```json fence). Throws <see cref="LlmException"/> otherwise.</summary>
    public static JsonElement ParseObject(string? text, string provider)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = t.IndexOf('\n', StringComparison.Ordinal);
            var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine > 0 && lastFence > firstNewLine) t = t[(firstNewLine + 1)..lastFence].Trim();
        }
        if (t.Length == 0) throw new LlmException(LlmFailureKind.InvalidResponse, $"{provider} returned an empty answer.");
        try
        {
            using var doc = JsonDocument.Parse(t);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new LlmException(LlmFailureKind.InvalidResponse, $"{provider} returned JSON that is not an object.");
            return doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new LlmException(LlmFailureKind.InvalidResponse, $"{provider} returned invalid JSON: {e.Message}", e);
        }
    }

    public static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A non-empty, trimmed string, or null for missing / empty / "null" / "none".</summary>
    public static string? GetText(JsonElement e, string name)
    {
        var s = GetString(e, name)?.Trim();
        return string.IsNullOrEmpty(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase) || s.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    public static double? GetDouble(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    public static int? GetInt(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        if (v.TryGetInt32(out var i)) return i;
        return v.TryGetDouble(out var d) && double.IsFinite(d) && Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < int.MaxValue ? (int)Math.Round(d) : null;
    }

    public static bool? GetBool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static IEnumerable<JsonElement> GetArray(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    public static IEnumerable<string> GetStrings(JsonElement e, string name) =>
        GetArray(e, name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0);

    /// <summary>A confidence in [0, 1], or null when missing / not finite / far outside the range (garbage).</summary>
    public static double? GetConfidence(JsonElement e, string name)
    {
        var d = GetDouble(e, name);
        if (d is not { } v || v < -0.05 || v > 1.05) return null;
        return Math.Round(Math.Clamp(v, 0, 1), 3);
    }

    public static Guid? ParseGuid(string? s) => Guid.TryParse(s, out var g) && g != Guid.Empty ? g : null;

    public static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParseExact(s?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static TimeOnly? ParseTime(string? s) =>
        TimeOnly.TryParseExact(s?.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    public static TEnum? ParseEnum<TEnum>(string? s) where TEnum : struct, System.Enum =>
        !string.IsNullOrWhiteSpace(s) && System.Enum.TryParse<TEnum>(s.Trim(), ignoreCase: true, out var v) && System.Enum.IsDefined(v) ? v : null;
}
