using FollowUp.Core;
using FollowUp.Core.Platform;
using Microsoft.Extensions.Hosting.WindowsServices;

// FollowUp Officer background processor (spec §58–§65): the always-on host for the durable job engine.
// Runs as the Windows service "FollowUpOfficer.Worker", under systemd or in a container. When the API also runs
// jobs, database leases make each job run on exactly one host.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.Services.AddWindowsService(o => o.ServiceName = "FollowUpOfficer.Worker");
builder.Services.AddSystemd();

HostDatabase.NormalisePaths(builder.Configuration, builder.Environment.ContentRootPath);
var connectionString = builder.Configuration.GetConnectionString("FollowUp");
var useSqlite = HostDatabase.UseSqlite(builder.Configuration, builder.Environment.ContentRootPath, ref connectionString);
builder.Services.AddFollowUpCore(builder.Configuration, "Worker", connectionString, useSqlite);

// INTEGRATION: AddMicrosoft365Integration + AddFollowUpAI are registered here after merge
// builder.Services.AddMicrosoft365Integration(builder.Configuration, connectionString);
// builder.Services.AddFollowUpAI(builder.Configuration);
builder.Services.AddFallbackPorts();

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FollowUp.Worker");

// Migrations retry while SQL Server is still starting (boot order). Seeders run in the API host only.
await StartupInitializer.InitialiseAsync(host.Services, logger, runSeeders: false);
await host.RunAsync();
