using FollowUp.Integrations.GraphEmulator;
using System.Text.Json;
using System.Text.Json.Serialization;
using FollowUp.Api.Endpoints;
using Microsoft.AspNetCore.SignalR;

namespace FollowUp.Api.Infrastructure;

/// <summary>Service registration and the HTTP pipeline of the API host.</summary>
public static class ApiSetup
{
    /// <summary>camelCase JSON, enums as strings (integers accepted on input), dictionary keys as written.</summary>
    public static void ConfigureJson(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DictionaryKeyPolicy = null;
        o.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: true));
    }

    public static IServiceCollection AddFollowUpApi(this IServiceCollection services, IConfiguration config)
    {
        services.ConfigureHttpJsonOptions(o => ConfigureJson(o.SerializerOptions));
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        services.AddSignalR().AddJsonProtocol(o => ConfigureJson(o.PayloadSerializerOptions));
        services.AddSingleton<IUserIdProvider, EmployeeUserIdProvider>();
        services.AddHostedService<UiEventRelay>();
        services.AddFollowUpAuth(config);
        var origins = config.GetSection("Cors:Origins").Get<string[]>() is { Length: > 0 } o ? o : ["http://localhost:4860"];
        services.AddCors(c => c.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        return services;
    }

    /// <summary>
    /// Pipeline: errors → security headers → static UI → routing → CORS → CSRF guard → authentication → actor
    /// (session re-validation, viewer scope) → authorization → rate limiting → endpoints → SPA fallback.
    /// </summary>
    public static WebApplication UseFollowUpApi(this WebApplication app)
    {
        app.UseMiddleware<ExceptionMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseCors();
        app.UseMiddleware<CsrfMiddleware>();
        app.UseAuthentication();
        app.UseMiddleware<ActorMiddleware>();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapFollowUpEndpoints();
        app.MapHub<EventsHub>("/hubs/events").RequireAuthorization();
        // The Graph-compatible emulator tenant (Microsoft365:Mode = Emulator) lives under /graph-emulator,
        // outside the /api CSRF guard and the SPA fallback.
        app.MapGraphEmulator();
        app.MapFallback(SpaFallbackAsync).AllowAnonymous();
        return app;
    }

    /// <summary>Client-side routes get index.html; unknown /api, /hubs and /graph-emulator paths get a 404 problem.</summary>
    private static async Task SpaFallbackAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs") || path.StartsWithSegments("/graph-emulator")
            || !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)))
        {
            await Problems.WriteAsync(ctx, StatusCodes.Status404NotFound, "Not found", "There is no such endpoint.", "not_found");
            return;
        }
        var env = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var index = env.WebRootFileProvider.GetFileInfo("index.html");
        if (!index.Exists)
        {
            await Problems.WriteAsync(ctx, StatusCodes.Status404NotFound, "Not found", "The web UI has not been built (wwwroot/index.html is missing).", "ui_missing");
            return;
        }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.SendFileAsync(index);
    }
}
