using FollowUp.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace FollowUp.Integrations.Graph;

/// <summary>
/// Business clock used for sync windows and emulator timestamps. Follows the application's <see cref="IClock"/>
/// (so a pinned demo clock is honoured) and falls back to the system clock when none is registered.
/// Token lifetimes use <see cref="TimeProvider"/> instead (real time).
/// </summary>
public sealed class Microsoft365Clock(IServiceProvider services)
{
    private IClock? _clock;

    public DateTime UtcNow => (_clock ??= services.GetService<IClock>() ?? new SystemClock()).UtcNow;
}
