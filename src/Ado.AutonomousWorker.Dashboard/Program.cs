using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Dashboard.Components;
using Ado.AutonomousWorker.Infrastructure;
using Microsoft.Extensions.Configuration;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Mirror the same config resolution as the CLI.
var userConfigPath = Path.Combine(StorageOptions.DefaultAppFolder, "config.jsonc");

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddJsonFile(userConfigPath, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("AdoWorker_");

builder.Services.AddAutonomousWorker(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
