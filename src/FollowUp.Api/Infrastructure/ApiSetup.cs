using FollowUp.Integrations.GraphEmulator;
using System.Text.Json;
using System.Text.Json.Serialization;
using FollowUp.Api.Endpoints;
using FollowUp.Core.Common;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

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
        // Sub-path hosting behind a reverse proxy ("PathBase": "/followup"). Under IIS the ASP.NET Core Module sets
        // the virtual application path itself, and UsePathBase is then a no-op.
        if (app.Configuration["PathBase"] is { Length: > 1 } pathBase) app.UsePathBase(pathBase.TrimEnd('/'));
        // IIS matches the application path case-insensitively but browsers compare cookie paths case-sensitively, so
        // /FollowupOfficer and /followupofficer would be separate sign-ins: FollowUp:PublicAppUrl names the one spelling.
        var publicUrl = app.Services.GetRequiredService<IOptions<FollowUpOptions>>().Value.PublicAppUrl;
        if (PublicPathBase(publicUrl) is { } publicPathBase) app.Use((ctx, next) => RedirectOtherSpellingAsync(ctx, next, publicPathBase));
        // index.html is always rendered by the SPA fallback (it writes the <base href> for the current path base),
        // so the static file middleware never serves it directly.
        app.Use((ctx, next) =>
        {
            if (ctx.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)) ctx.Request.Path = "/";
            return next(ctx);
        });
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

    /// <summary>The path of the public URL without its trailing slash, or null when the app is public at a site root.</summary>
    private static string? PublicPathBase(string publicUrl) =>
        Uri.TryCreate(publicUrl, UriKind.Absolute, out var url) && url.AbsolutePath.TrimEnd('/') is { Length: > 0 } path ? path : null;

    /// <summary>Page loads under another spelling of the virtual path move to the public one; writes are left alone.</summary>
    private static Task RedirectOtherSpellingAsync(HttpContext ctx, RequestDelegate next, string publicPathBase)
    {
        var pathBase = ctx.Request.PathBase.Value;
        var otherSpelling = string.Equals(pathBase, publicPathBase, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pathBase, publicPathBase, StringComparison.Ordinal);
        if (!otherSpelling || !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))) return next(ctx);
        ctx.Response.Redirect(publicPathBase + ctx.Request.Path + ctx.Request.QueryString);
        return Task.CompletedTask;
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
        // The same build works at a site root and under an IIS virtual application: <base href> follows the path base.
        string html;
        using (var reader = new StreamReader(index.CreateReadStream())) html = await reader.ReadToEndAsync(ctx.RequestAborted);
        var baseHref = (ctx.Request.PathBase.Value ?? "").TrimEnd('/') + "/";
        html = html.Replace("<base href=\"/\">", $"<base href=\"{System.Net.WebUtility.HtmlEncode(baseHref)}\">", StringComparison.OrdinalIgnoreCase);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.WriteAsync(html, ctx.RequestAborted);
    }
}
