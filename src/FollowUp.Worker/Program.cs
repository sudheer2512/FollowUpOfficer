using FollowUp.Core;
using FollowUp.Core.Platform;
using FollowUp.Integrations;
using FollowUp.AI;
using FollowUp.Worker;
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

// Registered first so the job engine starts on a migrated database. Hosted services start after the service has
// reported "running" to Windows, so a slow or unreachable SQL Server cannot trip the start time-out and hide the error.
builder.Services.AddHostedService<DatabaseStartup>();

HostDatabase.NormalisePaths(builder.Configuration, builder.Environment.ContentRootPath);
var connectionString = builder.Configuration.GetConnectionString("FollowUp");
var useSqlite = HostDatabase.UseSqlite(builder.Configuration, builder.Environment.ContentRootPath, ref connectionString);
builder.Services.AddFollowUpCore(builder.Configuration, "Worker", connectionString, useSqlite);

// Real ports: Microsoft Graph (or its emulator) and the AI layer. Fallbacks only fill gaps (TryAdd).
builder.Services.AddMicrosoft365Integration(builder.Configuration, connectionString!, useSqlite);
builder.Services.AddFollowUpAI(builder.Configuration);
builder.Services.AddFallbackPorts();

await builder.Build().RunAsync();
