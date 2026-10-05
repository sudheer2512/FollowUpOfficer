using System.Net.Http.Headers;
using System.Text.Json;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;

namespace FollowUp.Integrations.Graph;

/// <summary>Classifies Graph / token-endpoint failures into <see cref="IntegrationException"/> kinds and computes retry delays.</summary>
internal static class GraphErrors
{
    /// <summary>Error codes Graph uses when a delta / sync state can no longer be resumed.</summary>
    private static readonly HashSet<string> GoneCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "syncStateNotFound", "resyncRequired", "syncStateInvalid", "SyncStateNotFound", "ResyncRequired",
    };

    /// <summary>429, 503 and 504 are retried (other 5xx are reported as transient without a retry).</summary>
    public static bool IsRetryable(int status) => status is 429 or 503 or 504;

    public static bool IsGoneCode(string? code) => code is not null && GoneCodes.Contains(code);

    public static IntegrationErrorKind KindFor(int status, string? code)
    {
        if (IsGoneCode(code)) return IntegrationErrorKind.Gone;
        return status switch
        {
            401 => IntegrationErrorKind.Authentication,
            403 => IntegrationErrorKind.Permission,
            404 => IntegrationErrorKind.NotFound,
            410 => IntegrationErrorKind.Gone,
            429 => IntegrationErrorKind.Throttled,
            >= 500 => IntegrationErrorKind.Transient,
            >= 400 => IntegrationErrorKind.Validation,
            _ => IntegrationErrorKind.Unknown,
        };
    }

    public static IntegrationException Classify(string operation, int status, string? code, string? message, TimeSpan? retryAfter) =>
        new(KindFor(status, code),
            $"{operation} failed with HTTP {status}{(string.IsNullOrEmpty(code) ? "" : " " + code)}: {(string.IsNullOrWhiteSpace(message) ? "(no error message)" : message)}",
            status, status == 429 ? retryAfter ?? TimeSpan.FromSeconds(1) : retryAfter);

    /// <summary>Retry-After as a delta or an HTTP date.</summary>
    public static TimeSpan? ReadRetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        var value = headers.RetryAfter;
        if (value is null) return null;
        if (value.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (value.Date is { } date)
        {
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    /// <summary>Reads a Graph error ({"error":{"code","message"}}) or an OAuth error ({"error","error_description"}).</summary>
    public static async Task<(string? Code, string? Message)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return (null, response.ReasonPhrase);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                    var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                    return (code, message);
                }
                if (error.ValueKind == JsonValueKind.String)
                {
                    var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
                    return (error.GetString(), description);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (proxy / gateway page): fall through and report the raw text.
        }
        return (null, Truncate(text, 300));
    }

    /// <summary>Exponential backoff with jitter, capped.</summary>
    public static TimeSpan Backoff(int attempt, GraphOptions options)
    {
        var baseSeconds = Math.Max(0, options.RetryBaseDelaySeconds);
        var exp = baseSeconds * Math.Pow(2, Math.Min(attempt, 16));
        var jitter = Random.Shared.NextDouble() * baseSeconds;
        return Cap(TimeSpan.FromSeconds(exp + jitter), options);
    }

    public static TimeSpan Cap(TimeSpan delay, GraphOptions options)
    {
        var max = TimeSpan.FromSeconds(Math.Max(0, options.MaxRetryDelaySeconds));
        return delay > max ? max : delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    public static string Describe(Uri uri) => uri.AbsolutePath;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
