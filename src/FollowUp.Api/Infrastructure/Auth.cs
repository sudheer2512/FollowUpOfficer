using System.Security.Claims;
using System.Threading.RateLimiting;
using FollowUp.Core.Services;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace FollowUp.Api.Infrastructure;

/// <summary>Claim types issued by the API.</summary>
public static class FollowUpClaims
{
    public const string Employee = "fu:emp";
    public const string Account = "fu:acct";
    public const string Stamp = "fu:stamp";
    public const string Manager = "fu:mgr";
    public const string UserName = "fu:user";
}

/// <summary>Authorization policy names.</summary>
public static class Policies
{
    public const string Executive = "Executive";
    public const string Administrator = "Administrator";
    public const string ExecutiveOrAdmin = "ExecutiveOrAdmin";
    public const string Manager = "Manager";
}

/// <summary>
/// Authentication (spec §69): an HttpOnly cookie "fu.auth" for the web UI, plus Entra ID bearer tokens when
/// Entra:TenantId is configured. A policy scheme picks the bearer handler when an Authorization header (or a SignalR
/// access_token) is present. 401 / 403 are returned as problem+json, never as redirects.
/// </summary>
public static class AuthSetup
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string BearerScheme = JwtBearerDefaults.AuthenticationScheme;
    public const string SmartScheme = "FollowUp";
    public const string LoginRateLimit = "login";

    public static bool EntraEnabled(IConfiguration config) => !string.IsNullOrWhiteSpace(config["Entra:TenantId"]);

    public static IServiceCollection AddFollowUpAuth(this IServiceCollection services, IConfiguration config)
    {
        var entra = EntraEnabled(config);
        var auth = services.AddAuthentication(SmartScheme)
            .AddPolicyScheme(SmartScheme, "Cookie or Entra bearer", o => o.ForwardDefaultSelector = ctx =>
            {
                if (!entra) return CookieScheme;
                if (ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return BearerScheme;
                if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.ContainsKey("access_token")) return BearerScheme;
                return CookieScheme;
            })
            .AddCookie(CookieScheme, o =>
            {
                o.Cookie.Name = "fu.auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
                o.Events.OnRedirectToLogin = ctx => Problems.WriteAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized, "Not signed in", "Sign in to continue.", "unauthenticated");
                o.Events.OnRedirectToAccessDenied = ctx => Problems.WriteAsync(ctx.HttpContext, StatusCodes.Status403Forbidden, "Forbidden", "You are not allowed to do that.", "forbidden");
            });

        if (entra)
        {
            var tenant = config["Entra:TenantId"]!;
            var clientId = config["Entra:ClientId"];
            var audience = config["Entra:Audience"];
            auth.AddJwtBearer(BearerScheme, o =>
            {
                o.Authority = config["Entra:Authority"] is { Length: > 0 } a ? a : $"https://login.microsoftonline.com/{tenant}/v2.0";
                o.MapInboundClaims = false;
                o.TokenValidationParameters.ValidAudiences = new[] { audience, clientId, clientId is null ? null : $"api://{clientId}" }
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
                o.TokenValidationParameters.ValidIssuers = [$"https://login.microsoftonline.com/{tenant}/v2.0", $"https://sts.windows.net/{tenant}/"];
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.TryGetValue("access_token", out var token)) ctx.Token = token;
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async ctx =>
                    {
                        var p = ctx.Principal!;
                        var oid = p.FindFirst("oid")?.Value ?? p.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
                        var emails = new[] { p.FindFirst("preferred_username")?.Value, p.FindFirst("email")?.Value, p.FindFirst("upn")?.Value };
                        try
                        {
                            var accounts = ctx.HttpContext.RequestServices.GetRequiredService<AccountService>();
                            var session = await accounts.FindEntraAccountAsync(oid, emails, ctx.HttpContext.RequestAborted);
                            ctx.Principal = Principal(session, null, BearerScheme);
                        }
                        catch (AuthenticationFailedException ex) { ctx.Fail(ex.Message); }
                    },
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        await Problems.WriteAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized, "Not signed in", ctx.ErrorDescription ?? "A valid token is required.", "unauthenticated");
                    },
                    OnForbidden = ctx => Problems.WriteAsync(ctx.HttpContext, StatusCodes.Status403Forbidden, "Forbidden", "You are not allowed to do that.", "forbidden"),
                };
            });
        }

        services.AddAuthorization(o =>
        {
            o.AddPolicy(Policies.Executive, p => p.RequireAuthenticatedUser().RequireRole(nameof(AppRole.Executive)));
            o.AddPolicy(Policies.Administrator, p => p.RequireAuthenticatedUser().RequireRole(nameof(AppRole.Administrator)));
            o.AddPolicy(Policies.ExecutiveOrAdmin, p => p.RequireAuthenticatedUser().RequireRole(nameof(AppRole.Executive), nameof(AppRole.Administrator)));
            o.AddPolicy(Policies.Manager, p => p.RequireAuthenticatedUser().RequireAssertion(c =>
                c.User.IsInRole(nameof(AppRole.Executive)) || c.User.IsInRole(nameof(AppRole.SeniorManager)) || c.User.IsInRole(nameof(AppRole.TeamLeader))
                || c.User.HasClaim(FollowUpClaims.Manager, "true")));
        });

        var perMinute = config.GetValue("Auth:LoginAttemptsPerMinute", 20);
        services.AddRateLimiter(o =>
        {
            o.AddPolicy(LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, perMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.OnRejected = (ctx, _) => new ValueTask(Problems.WriteAsync(ctx.HttpContext, StatusCodes.Status429TooManyRequests, "Too many requests",
                "Too many sign-in attempts from this address. Wait a minute and try again.", "rate_limited"));
        });
        return services;
    }

    /// <summary>The principal for a validated session; roles come from the database, never from the client.</summary>
    public static ClaimsPrincipal Principal(SessionInfo s, Viewer? viewer, string authenticationType)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, s.EmployeeId.ToString()),
            new(FollowUpClaims.Employee, s.EmployeeId.ToString()),
            new(FollowUpClaims.Account, s.AccountId.ToString()),
            new(FollowUpClaims.Stamp, s.SecurityStamp.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(FollowUpClaims.UserName, s.UserName),
            new(ClaimTypes.Name, s.Name),
        };
        foreach (var role in Enum.GetValues<AppRole>().Where(r => r != AppRole.None && s.Roles.HasFlag(r)))
            claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
        if (viewer?.IsManagement == true) claims.Add(new Claim(FollowUpClaims.Manager, "true"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static async Task SignInAsync(HttpContext http, SessionInfo s, Viewer viewer) =>
        await http.SignInAsync(CookieScheme, Principal(s, viewer, CookieScheme), new AuthenticationProperties { AllowRefresh = true, IsPersistent = false });
}
