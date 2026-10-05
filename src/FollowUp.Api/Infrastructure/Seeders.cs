using FollowUp.Core.Common;
using FollowUp.Core.Platform;
using FollowUp.Core.Seed;
using FollowUp.Domain;
using FollowUp.Integrations.GraphEmulator;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FollowUp.Api.Infrastructure;

/// <summary>Start-up seeding for the API host: emulator tenant schema → configuration → demo history.</summary>
public static class SeedingRegistration
{
    public static IServiceCollection AddFollowUpSeeders(this IServiceCollection services)
    {
        // A pinnable clock lets the demo seeder replay history through the real services; unpinned it is the system clock.
        services.TryAddSingleton<IClock>(new MutableClock());
        services.AddSingleton<IStartupSeeder, EmulatorDatabaseSeeder>();
        services.AddSingleton<IStartupSeeder, ConfigurationSeeder>();
        services.AddSingleton<IStartupSeeder, DemoDataSeeder>();
        return services;
    }
}

/// <summary>Creates / migrates the Microsoft 365 emulator tenant schema (Emulator mode only).</summary>
internal sealed class EmulatorDatabaseSeeder : IStartupSeeder
{
    public int Order => 0;
    public Task SeedAsync(IServiceProvider sp, CancellationToken ct) => GraphEmulatorDatabase.MigrateAsync(sp, ct);
}

/// <summary>Calendars, holidays, categories, escalation ladders and templates (idempotent).</summary>
internal sealed class ConfigurationSeeder(ILogger<ConfigurationSeeder> logger) : IStartupSeeder
{
    public int Order => 10;
    public Task SeedAsync(IServiceProvider sp, CancellationToken ct) => ConfigSeeder.RunAsync(sp, logger, ct);
}

/// <summary>Demo organisation and four weeks of history on an empty database (FollowUp:SeedDemoData).</summary>
internal sealed class DemoDataSeeder(IOptions<FollowUpOptions> options, ILogger<DemoDataSeeder> logger) : IStartupSeeder
{
    public int Order => 20;
    public Task SeedAsync(IServiceProvider sp, CancellationToken ct) =>
        options.Value.SeedDemoData ? DemoSeeder.RunAsync(sp, logger, ct) : Task.CompletedTask;
}
