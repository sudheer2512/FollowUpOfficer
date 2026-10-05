using FollowUp.Core.Abstractions;
using FollowUp.Integrations.Disabled;
using FollowUp.Integrations.Graph;
using FollowUp.Integrations.GraphEmulator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FollowUp.Integrations;

/// <summary>Registers the Microsoft 365 ports.</summary>
public static class Microsoft365ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Microsoft 365 integration from configuration section "Microsoft365".
    /// Graph / Emulator: the Graph REST client and the Graph-backed ports (only base URLs differ); Emulator additionally
    /// registers the emulator store (<paramref name="connectionString"/>, SQL Server or SQLite) and <see cref="IDemoTenant"/>.
    /// Disabled: no-op ports. <see cref="GraphOptions"/> is available through IOptions.
    /// </summary>
    public static IServiceCollection AddMicrosoft365Integration(this IServiceCollection services, IConfiguration configuration,
        string connectionString, bool useSqlite = false)
    {
        var section = configuration.GetSection(GraphOptions.SectionName);
        services.AddOptions<GraphOptions>()
            .Bind(section)
            .PostConfigure(o => o.ApplyDefaults())
            .Validate(o => o.Mode == Microsoft365Mode.Disabled || Uri.TryCreate(o.ResolvedGraphBaseUrl, UriKind.Absolute, out _),
                "Microsoft365:GraphBaseUrl must be an absolute URL.")
            .Validate(o => o.Mode == Microsoft365Mode.Disabled || Uri.TryCreate(o.ResolvedAuthorityHost, UriKind.Absolute, out _),
                "Microsoft365:AuthorityHost must be an absolute URL.");

        var mode = section.GetValue<Microsoft365Mode?>(nameof(GraphOptions.Mode)) ?? Microsoft365Mode.Emulator;
        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<Microsoft365Clock>();

        if (mode == Microsoft365Mode.Disabled)
        {
            services.AddSingleton<IEmailService, DisabledEmailService>();
            services.AddSingleton<ITeamsService, DisabledTeamsService>();
            services.AddSingleton<IMailSynchronizationService, DisabledMailSynchronizationService>();
            services.AddSingleton<ITeamsSynchronizationService, DisabledTeamsSynchronizationService>();
            services.AddSingleton<ICalendarService, DisabledCalendarService>();
            services.AddSingleton<IUserDirectoryService, DisabledUserDirectoryService>();
            services.AddSingleton<IIntegrationHealth, DisabledIntegrationHealth>();
            return services;
        }

        services.AddHttpClient(GraphClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient(GraphTokenProvider.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.TryAddSingleton<IGraphCredentialSource, OptionsGraphCredentialSource>();
        services.AddSingleton<GraphTokenProvider>();
        services.AddSingleton<GraphClient>();

        services.AddSingleton<GraphEmailService>();
        services.AddSingleton<IEmailService>(sp => sp.GetRequiredService<GraphEmailService>());
        services.AddSingleton<GraphTeamsService>();
        services.AddSingleton<ITeamsService>(sp => sp.GetRequiredService<GraphTeamsService>());
        services.AddSingleton<GraphMailSyncService>();
        services.AddSingleton<IMailSynchronizationService>(sp => sp.GetRequiredService<GraphMailSyncService>());
        services.AddSingleton<GraphDirectoryService>();
        services.AddSingleton<IUserDirectoryService>(sp => sp.GetRequiredService<GraphDirectoryService>());
        services.AddSingleton<GraphTeamsSyncService>();
        services.AddSingleton<ITeamsSynchronizationService>(sp => sp.GetRequiredService<GraphTeamsSyncService>());
        services.AddSingleton<GraphCalendarService>();
        services.AddSingleton<ICalendarService>(sp => sp.GetRequiredService<GraphCalendarService>());
        services.AddSingleton<GraphIntegrationHealth>();
        services.AddSingleton<IIntegrationHealth>(sp => sp.GetRequiredService<GraphIntegrationHealth>());

        if (mode == Microsoft365Mode.Emulator)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("The Microsoft 365 emulator needs a database connection string.");
            services.AddDbContext<GraphEmulatorDbContext>(o => GraphEmulatorDbContext.Configure(o, connectionString, useSqlite));
            services.AddSingleton<GraphEmulatorState>();
            services.AddScoped(sp => new GraphEmulatorStore(sp.GetRequiredService<GraphEmulatorDbContext>(),
                sp.GetRequiredService<GraphEmulatorState>(), sp.GetRequiredService<IOptions<GraphOptions>>(),
                sp.GetRequiredService<Microsoft365Clock>(), sp.GetRequiredService<TimeProvider>()));
            services.AddScoped(sp => new GraphEmulatorTenant(sp.GetRequiredService<GraphEmulatorStore>()));
            services.AddScoped<IDemoTenant>(sp => sp.GetRequiredService<GraphEmulatorTenant>());
        }
        return services;
    }
}
