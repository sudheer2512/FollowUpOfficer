using FollowUp.Api.Infrastructure;
using FollowUp.Core;
using FollowUp.Core.Platform;
using FollowUp.Integrations;
using FollowUp.AI;
using Microsoft.Extensions.Hosting.WindowsServices;

// FollowUp Officer — HTTP API, SignalR hub and Angular host (spec §59: also installable as the Windows service "FollowUpOfficer.Api").
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Windows service and IIS (APP_POOL_ID is set for IIS worker processes): the content root is the published folder.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() || Environment.GetEnvironmentVariable("APP_POOL_ID") is not null
        ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService(o => o.ServiceName = "FollowUpOfficer.Api");

HostDatabase.NormalisePaths(builder.Configuration, builder.Environment.ContentRootPath);
var connectionString = builder.Configuration.GetConnectionString("FollowUp");
var useSqlite = HostDatabase.UseSqlite(builder.Configuration, builder.Environment.ContentRootPath, ref connectionString);
builder.Services.AddFollowUpSeeders();
builder.Services.AddFollowUpCore(builder.Configuration, "Api", connectionString, useSqlite);

// Real ports: Microsoft Graph (or its emulator) and the AI layer. Fallbacks only fill gaps (TryAdd).
builder.Services.AddMicrosoft365Integration(builder.Configuration, connectionString!, useSqlite);
builder.Services.AddFollowUpAI(builder.Configuration);
builder.Services.AddFallbackPorts();

builder.Services.AddFollowUpApi(builder.Configuration);

var app = builder.Build();

await StartupInitializer.InitialiseAsync(app.Services, app.Logger, runSeeders: true);

app.UseFollowUpApi();
app.Run();

/// <summary>Entry point marker for WebApplicationFactory-based tests.</summary>
public partial class Program;
