using FollowUp.Api.Infrastructure;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FollowUp.Api.Endpoints;

/// <summary>/api/auth — sign-in configuration, cookie login / logout, the current user.</summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth").WithTags("Auth");

        g.MapGet("/config", async (IConfiguration config, IHostEnvironment env, IOptions<FollowUpOptions> options, AccountService accounts, CancellationToken ct) =>
        {
            var entra = AuthSetup.EntraEnabled(config);
            var clientId = config["Entra:ClientId"];
            var audience = config["Entra:Audience"] is { Length: > 0 } a ? a : clientId is null ? null : $"api://{clientId}";
            var scope = config["Entra:ApiScope"] is { Length: > 0 } s ? s : audience is null ? null : $"{audience.TrimEnd('/')}/access_as_user";
            var demo = (options.Value.SeedDemoData && env.IsDevelopment()) || config.GetValue<bool>("FollowUp:ShowDemoUsers");
            return new AuthConfigDto(entra, entra ? config["Entra:TenantId"] : null, entra ? clientId : null, entra ? scope : null, demo,
                demo ? await accounts.DemoUsersAsync(ct) : []);
        }).AllowAnonymous();

        g.MapPost("/login", async (LoginRequest r, AccountService accounts, FollowUpDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var session = await accounts.LoginAsync(r.UserName, r.Password, ct);
            var viewer = await ViewerFactory.BuildAsync(db, session.EmployeeId, session.Roles, ct);
            await AuthSetup.SignInAsync(http, session, viewer);
            return await accounts.MeAsync(session, viewer, ct);
        }).AllowAnonymous().RequireRateLimiting(AuthSetup.LoginRateLimit);

        g.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(AuthSetup.CookieScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        g.MapGet("/me", (HttpContext http, AccountService accounts, ActorContext actor, CancellationToken ct) =>
            accounts.MeAsync(ActorMiddleware.Session(http), actor.Viewer, ct));
    }
}
