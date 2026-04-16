using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Dashboard.Components;
using Ado.AutonomousWorker.Infrastructure;
using Ado.AutonomousWorker.Infrastructure.Logging;
using MudBlazor.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Mirror the same config resolution as the CLI.
var userConfigPath = Path.Combine(StorageOptions.DefaultAppFolder, "config.jsonc");

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddJsonFile(userConfigPath, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("AdoWorker_");

var storageBaseOverride = StorageOptions.GetDevStorageOverride(builder.Environment.IsDevelopment());
builder.Host.UseSerilog(WorkerLoggingSetup.CreateLogger(builder.Configuration, storageBaseOverride, logFilePrefix: "dashboard"), dispose: true);

builder.Services.AddAutonomousWorker(builder.Configuration, storageBaseOverride);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseSerilogRequestLogging();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
