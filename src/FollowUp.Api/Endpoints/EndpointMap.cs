using FollowUp.Core.Contracts;
using FollowUp.Core.Services;

namespace FollowUp.Api.Endpoints;

/// <summary>Maps every /api route (docs/API.md). The /api group requires an authenticated user unless marked otherwise.</summary>
public static class EndpointMap
{
    public static void MapFollowUpEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapAuthEndpoints();
        api.MapSystemEndpoints();
        api.MapTaskEndpoints();
        api.MapExecutiveEndpoints();
        api.MapRecurringEndpoints();
        api.MapMessageEndpoints();
        api.MapPlanningEndpoints();
        api.MapAdminEndpoints();
        api.MapSimulatorEndpoints();
    }
}

/// <summary>Small helpers shared by the endpoint modules.</summary>
internal static class Http
{
    /// <summary>"a,b,c" → GUIDs (invalid entries rejected with 400).</summary>
    public static List<Guid> Guids(string? csv)
    {
        var result = new List<Guid>();
        foreach (var part in (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            result.Add(Guid.TryParse(part, out var g) ? g : throw new Core.Common.ValidationException("invalid_id", $"'{part}' is not a valid id."));
        return result;
    }

    /// <summary>"A,B" → enum values (case-insensitive; invalid entries rejected with 400).</summary>
    public static List<T> Enums<T>(string? csv) where T : struct, Enum
    {
        var result = new List<T>();
        foreach (var part in (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            result.Add(Enum.TryParse<T>(part, true, out var v) ? v : throw new Core.Common.ValidationException("invalid_value", $"'{part}' is not a valid {typeof(T).Name}."));
        return result;
    }

    /// <summary>Runs a task command, then returns the refreshed task detail.</summary>
    public static async Task<TaskDetail> Detail(TaskQueryService queries, Guid id, Task command, CancellationToken ct)
    {
        await command;
        return await queries.GetDetailAsync(id, ct);
    }
}
