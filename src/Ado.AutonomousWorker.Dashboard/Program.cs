using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Dashboard.Components;
using Ado.AutonomousWorker.Infrastructure;
using Ado.AutonomousWorker.Infrastructure.Logging;
using MudBlazor.Services;
using Serilog;

// When run as a dotnet tool (via `dotnet <dll>`), ASP.NET Core defaults the content root to
// Directory.GetCurrentDirectory() — the user's shell CWD — not the tool's installation directory.
// Explicitly set it to AppContext.BaseDirectory so wwwroot and static assets are always found.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

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
