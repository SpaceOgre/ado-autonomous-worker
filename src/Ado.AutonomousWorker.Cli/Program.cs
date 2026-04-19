using Ado.AutonomousWorker.Cli;
using Ado.AutonomousWorker.Cli.Commands;
using Ado.AutonomousWorker.Cli.Completion;
using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Infrastructure;
using Ado.AutonomousWorker.Infrastructure.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Spectre.Console;
using Spectre.Console.Cli;

var builder = Host.CreateApplicationBuilder(args);

var userConfigPath = Path.Combine(StorageOptions.DefaultAppFolder, "config.jsonc");

builder
    .Configuration.SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddJsonFile(userConfigPath, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("AdoWorker_");

var storageBaseOverride = StorageOptions.GetDevStorageOverride(builder.Environment.IsDevelopment());
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(WorkerLoggingSetup.CreateLogger(builder.Configuration, storageBaseOverride), dispose: true);

builder.Services.AddAutonomousWorker(builder.Configuration, storageBaseOverride);
builder.Services.AddSingleton<BootstrapCommand>();
builder.Services.AddSingleton<DoctorCommand>();
builder.Services.AddSingleton<QueueCommand>();
builder.Services.AddSingleton<ExecutionsCommand>();
builder.Services.AddSingleton<ShowExecutionCommand>();
builder.Services.AddSingleton<PowerShellCompletionCommand>();
builder.Services.AddSingleton<RunCommand>();
builder.Services.AddSingleton<RunTaskCommand>();
builder.Services.AddSingleton<ResumeTaskCommand>();
builder.Services.AddSingleton<UiRunPreviewCommand>();
builder.Services.AddSingleton<UiSplashCommand>();
builder.Services.AddSingleton<VersionCommand>();
builder.Services.AddSingleton<ResetCommand>();
builder.Services.AddSingleton<AgentTestCommand>();
builder.Services.AddSingleton<AzureDevOpsCommentTestCommand>();
builder.Services.AddSingleton<SetPhaseCommand>();
builder.Services.AddSingleton<DeployCommand>();
builder.Services.AddSingleton<InteractiveCommand>();
builder.Services.AddSingleton<CliCompletionService>();
builder.Services.AddSingleton<ExecutionLiveDisplay>();
builder.Services.AddSingleton<TerminalTitleService>();

using var host = builder.Build();

if (args.Length > 0 && string.Equals(args[0], "__complete", StringComparison.Ordinal))
{
    var currentWord = string.Empty;
    for (var index = 1; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], "--current", StringComparison.Ordinal))
        {
            currentWord = args[index + 1];
            break;
        }
    }

    var separatorIndex = Array.IndexOf(args, "--");
    var tokens = separatorIndex >= 0 ? args.Skip(separatorIndex + 1).ToArray() : [];

    var completionService = host.Services.GetRequiredService<CliCompletionService>();
    var suggestions = await completionService.GetSuggestionsAsync(currentWord, tokens, CancellationToken.None);
    foreach (var suggestion in suggestions)
    {
        Console.WriteLine(suggestion);
    }

    return 0;
}

// Launch interactive mode when invoked with no arguments in an interactive terminal.
if (args.Length == 0 && AnsiConsole.Profile.Capabilities.Interactive)
{
    args = ["interactive"];
}

var registrar = new TypeRegistrar(host.Services);
var app = new CommandApp(registrar);
app.Configure(config =>
{
    CliAppConfiguration.Configure(config);
    config
        .AddCommand<InteractiveCommand>("interactive")
        .WithDescription("Launch the interactive REPL with slash commands and tab completion.");
});

return await app.RunAsync(args);
