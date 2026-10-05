using FollowUp.Integrations.GraphEmulator;
using FollowUp.Api.Infrastructure;

namespace FollowUp.Api.Endpoints;

/// <summary>
/// Microsoft 365 simulator console (Emulator mode only; Executive or Administrator), mounted under /api/m365-sim.
/// The routes themselves are provided by FollowUp.Integrations (<c>MapGraphEmulatorConsole</c>).
/// </summary>
public static class SimulatorEndpoints
{
    public static RouteGroupBuilder MapSimulatorEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/m365-sim").WithTags("M365 simulator").RequireAuthorization(Policies.ExecutiveOrAdmin);
        // Emulator console (returns 404 problems outside Emulator mode).
        group.MapGraphEmulatorConsole();
        return group;
    }
}
