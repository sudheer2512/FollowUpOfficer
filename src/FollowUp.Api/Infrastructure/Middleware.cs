using System.Security.Claims;
using System.Text.Json;
using FollowUp.Core.Common;
using FollowUp.Core.Data;
using FollowUp.Core.Services;
using FollowUp.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FollowUp.Api.Infrastructure;

/// <summary>RFC 7807 problem+json responses: { type, title, status, detail, code, traceId }.</summary>
public static class Problems
{
    public static async Task WriteAsync(HttpContext ctx, int status, string title, string? detail, string code)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.Clear();
        ctx.Response.StatusCode = status;
        var options = ctx.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await ctx.Response.WriteAsJsonAsync(new ProblemBody($"https://httpstatuses.io/{status}", title, status, detail, code, ctx.TraceIdentifier),
            options, "application/problem+json");
    }

    private sealed record ProblemBody(string Type, string Title, int Status, string? Detail, string Code, string TraceId);
}

/// <summary>Maps domain / application exceptions to problem+json; unexpected errors become a 500 without internals.</summary>
public sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 499;
        }
        catch (Exception ex)
        {
            var (status, title, code, detail) = ex switch
            {
                NotFoundException e => (404, "Not found", "not_found", e.Message),
                ForbiddenException e => (403, "Forbidden", "forbidden", e.Message),
                ValidationException e => (400, "Validation failed", e.Code, e.Message),
                ConflictException e => (409, "Conflict", e.Code, e.Message),
                DomainException e => (400, "Not allowed", e.Code, e.Message),
                AuthenticationFailedException e => (401, "Sign-in failed", e.Code, e.Message),
                DbUpdateConcurrencyException => (409, "Conflict", "concurrent_update", "The record was changed by someone else. Reload and try again."),
                BadHttpRequestException e => (e.StatusCode, "Bad request", "invalid_request", "The request could not be read. Check the JSON body and parameters."),
                JsonException => (400, "Bad request", "invalid_json", "The request body is not valid JSON for this operation."),
                _ => (500, "Server error", "server_error", "An unexpected error occurred. It has been logged."),
            };
            if (status >= 500) logger.LogError(ex, "Unhandled error on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            else logger.LogDebug(ex, "Request failed with {Status} ({Code})", status, code);
            await Problems.WriteAsync(ctx, status, title, detail, code);
        }
    }
}

/// <summary>CSRF guard: mutating /api requests must carry "X-Requested-With: FollowUp" (a header browsers never add cross-site).</summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string Header = "X-Requested-With";
    public const string Value = "FollowUp";

    public Task InvokeAsync(HttpContext ctx)
    {
        var m = ctx.Request.Method;
        if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(m) && !HttpMethods.IsHead(m) && !HttpMethods.IsOptions(m)
            && !string.Equals(ctx.Request.Headers[Header].ToString(), Value, StringComparison.Ordinal))
            return Problems.WriteAsync(ctx, StatusCodes.Status403Forbidden, "Missing CSRF header",
                $"Requests that change data must send the header '{Header}: {Value}'.", "csrf_header_missing");
        return next(ctx);
    }
}

/// <summary>Browser hardening headers and a content security policy allowing this origin and its WebSocket.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string Csp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' data: https://fonts.gstatic.com; img-src 'self' data: blob:; connect-src 'self' ws: wss:; frame-ancestors 'none'; " +
        "base-uri 'self'; form-action 'self'; object-src 'none'";

    public Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Content-Security-Policy"] = Csp;
            h["Permissions-Policy"] = "camera=(), geolocation=(), microphone=(self)";
            if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        return next(ctx);
    }
}

/// <summary>
/// Turns the authenticated principal into the request's <see cref="ActorContext"/>: re-validates the account
/// (enabled, security stamp unchanged), builds the <see cref="Domain.Rules.Viewer"/> scope once per request and
/// refreshes role claims from the database so authorization policies never trust stale cookies.
/// </summary>
public sealed class ActorMiddleware(RequestDelegate next)
{
    public const string SessionItem = "fu.session";

    public async Task InvokeAsync(HttpContext ctx, ActorContext actor, AccountService accounts, FollowUpDbContext db)
    {
        var user = ctx.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var ok = Guid.TryParse(user.FindFirst(FollowUpClaims.Account)?.Value, out var accountId)
                & int.TryParse(user.FindFirst(FollowUpClaims.Stamp)?.Value, out var stamp);
            var session = ok ? await accounts.ValidateAsync(accountId, stamp, ctx.RequestAborted) : null;
            if (session is null)
            {
                if (user.Identity.AuthenticationType == AuthSetup.CookieScheme) await ctx.SignOutAsync(AuthSetup.CookieScheme);
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
            else
            {
                var viewer = await ViewerFactory.BuildAsync(db, session.EmployeeId, session.Roles, ctx.RequestAborted);
                actor.SetUser(session.EmployeeId, session.Name, viewer);
                ctx.Items[SessionItem] = session;
                ctx.User = AuthSetup.Principal(session, viewer, user.Identity.AuthenticationType ?? AuthSetup.CookieScheme);
            }
        }
        await next(ctx);
    }

    public static SessionInfo Session(HttpContext ctx) =>
        ctx.Items[SessionItem] as SessionInfo ?? throw new ForbiddenException("Sign in to continue.");
}
