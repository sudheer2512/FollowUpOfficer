using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using FollowUp.Core.Data;
using FollowUp.Core.Jobs;
using FollowUp.Core.Platform;
using FollowUp.Core.Services;
using FollowUp.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FollowUp.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the application core: database, services, background jobs and the durable job engine.
    /// The integration (Microsoft Graph) and AI ports must be registered by the host.
    /// </summary>
    public static IServiceCollection AddFollowUpCore(this IServiceCollection services, IConfiguration configuration, string role,
        string? connectionString = null, bool useSqlite = false)
    {
        services.Configure<FollowUpOptions>(configuration.GetSection(FollowUpOptions.Section));
        services.Configure<JobEngineOptions>(configuration.GetSection(JobEngineOptions.Section));
        var cs = connectionString ?? configuration.GetConnectionString("FollowUp") ?? Data.Database.DefaultConnectionString;

        services.AddDbContext<FollowUpDbContext>(o =>
        {
            if (useSqlite) o.UseSqlite(cs);
            // No EF retrying strategy: units of work use explicit transactions, and resilience comes from the job
            // engine (failed runs retry with backoff and resume from persisted checkpoints).
            else o.UseSqlServer(cs, sql => sql.CommandTimeout(60));
        });

        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton(new HostIdentity(role));
        services.AddSingleton<CalendarProvider>();
        services.AddScoped<ActorContext>();

        var keysPath = configuration["DataProtection:KeysPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "keys");
        var dp = services.AddDataProtection().SetApplicationName("FollowUpOfficer").PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<IFileStorage, EncryptedFileStorage>();

        services.AddScoped<AuditService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddScoped<MonitoringService>();
        services.AddScoped<TaskProjector>();
        services.AddScoped<TaskService>();
        services.AddScoped<ITaskService>(sp => sp.GetRequiredService<TaskService>());
        services.AddScoped<FollowUpService>();
        services.AddScoped<IReminderService>(sp => sp.GetRequiredService<FollowUpService>());
        services.AddScoped<EscalationService>();
        services.AddScoped<IEscalationService>(sp => sp.GetRequiredService<EscalationService>());
        services.AddScoped<CommitmentService>();
        services.AddScoped<RecurringTaskService>();
        services.AddScoped<IRecurringTaskService>(sp => sp.GetRequiredService<RecurringTaskService>());
        services.AddScoped<CommunicationService>();
        services.AddScoped<IntegrationErrorRecorder>();
        services.AddScoped<SyncService>();
        services.AddScoped<ISynchronizationCheckpointService>(sp => sp.GetRequiredService<SyncService>());
        services.AddScoped<NotificationDispatcher>();

        services.AddScoped<IBackgroundJob, RecurrenceJob>();
        services.AddScoped<IBackgroundJob, FollowUpJob>();
        services.AddScoped<IBackgroundJob, DeadlineJob>();
        services.AddScoped<IBackgroundJob, EscalationJob>();
        services.AddScoped<IBackgroundJob, CommitmentJob>();
        services.AddScoped<IBackgroundJob, SourceDiscoveryJob>();
        services.AddScoped<IBackgroundJob, MailSyncJob>();
        services.AddScoped<IBackgroundJob, TeamsSyncJob>();
        services.AddScoped<IBackgroundJob, CommunicationJob>();
        services.AddScoped<IBackgroundJob, NotificationJob>();
        services.AddScoped<IBackgroundJob, AttentionJob>();
        services.AddScoped<IBackgroundJob, MaintenanceJob>();
        services.AddHostedService<JobEngine>();
        return services;
    }
}
