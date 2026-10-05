using FollowUp.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FollowUp.Core.Platform;

/// <summary>
/// Startup data hook (demo organisation, reference data…). Every registered seeder runs once per host start, in
/// <see cref="Order"/>, after the database has been migrated. Seeders must be idempotent.
/// </summary>
public interface IStartupSeeder
{
    int Order { get; }
    Task SeedAsync(IServiceProvider sp, CancellationToken ct);
}

/// <summary>Host start-up: migrate the database (with retry), run the seeders, then load runtime settings.</summary>
public static class StartupInitializer
{
    public static async Task InitialiseAsync(IServiceProvider services, ILogger logger, bool runSeeders, CancellationToken ct = default)
    {
        await Data.Database.MigrateAsync(services, logger, ct);

        if (runSeeders)
        {
            using var scope = services.CreateScope();
            foreach (var seeder in scope.ServiceProvider.GetServices<IStartupSeeder>().OrderBy(s => s.Order))
            {
                try
                {
                    using var seedScope = services.CreateScope();
                    await seeder.SeedAsync(seedScope.ServiceProvider, ct);
                    logger.LogInformation("Seeder {Seeder} completed", seeder.GetType().Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Seeder {Seeder} failed; the application continues without its data", seeder.GetType().Name);
                }
            }
        }

        try
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SettingsService>().GetAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Runtime settings could not be loaded; using configured defaults");
        }
    }
}
