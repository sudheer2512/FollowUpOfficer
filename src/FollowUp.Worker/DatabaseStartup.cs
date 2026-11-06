using FollowUp.Core.Platform;

namespace FollowUp.Worker;

/// <summary>
/// Migrates the database (retrying while SQL Server is still starting) before the job engine starts. A failure stops
/// the host, so the real error reaches the log. Seeders run in the API host only.
/// </summary>
internal sealed class DatabaseStartup(IServiceProvider services, ILogger<DatabaseStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        StartupInitializer.InitialiseAsync(services, logger, runSeeders: false, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
