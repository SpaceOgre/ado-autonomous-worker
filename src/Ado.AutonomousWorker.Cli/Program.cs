using Ado.AutonomousWorker.Cli;
using Ado.AutonomousWorker.Cli.Commands;
using Ado.AutonomousWorker.Cli.Completion;
using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Spectre.Console.Cli;

var builder = Host.CreateApplicationBuilder(args);

var userConfigPath = Path.Combine(StorageOptions.DefaultAppFolder, "config.jsonc");

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddJsonFile(userConfigPath, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("AdoWorker_");

var earlyStorageOptions = new StorageOptions();
builder.Configuration.GetSection("Storage").Bind(earlyStorageOptions);
earlyStorageOptions.NormalizePaths();
Directory.CreateDirectory(earlyStorageOptions.LogRoot);
var logFilePath = Path.Combine(earlyStorageOptions.LogRoot, "worker-.log");

var configuredLevel = builder.Configuration["Serilog:ConsoleLevel"];
var consoleLevel = Enum.TryParse<LogEventLevel>(configuredLevel, ignoreCase: true, out var parsed)
    ? parsed
    : LogEventLevel.Warning;

var serilogLogger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
    .WriteTo.Console(
        restrictedToMinimumLevel: consoleLevel,
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        logFilePath,
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(serilogLogger, dispose: true);

builder.Services.AddAutonomousWorker(builder.Configuration);
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
    var tokens = separatorIndex >= 0
        ? args.Skip(separatorIndex + 1).ToArray()
        : [];

    var completionService = host.Services.GetRequiredService<CliCompletionService>();
    var suggestions = await completionService.GetSuggestionsAsync(currentWord, tokens, CancellationToken.None);
    foreach (var suggestion in suggestions)
    {
        Console.WriteLine(suggestion);
    }

    return 0;
}

var registrar = new TypeRegistrar(host.Services);
var app = new CommandApp(registrar);
app.Configure(config =>
{
    config.SetApplicationName("ado-worker");
    config.AddCommand<BootstrapCommand>("bootstrap").WithDescription("Initialize storage and workspace directories.");
    config.AddCommand<ResetCommand>("reset").WithDescription("Delete the local worker database.");
    config.AddCommand<DoctorCommand>("doctor").WithDescription("Verify configuration, repos, CLI tools, and Azure DevOps access.");
    config.AddCommand<QueueCommand>("queue").WithDescription("List eligible AI-Work and AI-Rework items.");
    config.AddCommand<ExecutionsCommand>("executions").WithDescription("List local task executions.");
    config.AddCommand<RunCommand>("run").WithDescription("Preview the selected queue, then process it and stop on the first failure.");
    config.AddCommand<VersionCommand>("version").WithDescription("Show the installed ado-worker version and runtime information.");
    config.AddBranch("completion", completion =>
    {
        completion.AddCommand<PowerShellCompletionCommand>("powershell").WithDescription("Print a PowerShell tab-completion registration script.");
    });

    config.AddBranch("execution", execution =>
    {
        execution.AddCommand<ShowExecutionCommand>("show").WithDescription("Show one local execution with events and agent sessions.");
    });

    config.AddBranch("task", task =>
    {
        task.AddCommand<RunTaskCommand>("run").WithDescription("Run a specific Azure DevOps work item.");
        task.AddCommand<ResumeTaskCommand>("resume").WithDescription("Resume a prior execution by ID.");
        task.AddCommand<SetPhaseCommand>("set-phase").WithDescription("Rewind an execution to an earlier phase so it can be resumed from there.");
    });

    config.AddBranch("agent", agent =>
    {
        agent.AddCommand<AgentTestCommand>("test").WithDescription("Send a test prompt to each configured agent to verify connectivity.");
    });

    config.AddBranch("ado", ado =>
    {
        ado.AddCommand<AzureDevOpsCommentTestCommand>("comment-test").WithDescription("Post a sample markdown comment to a work item to verify rendering.");
    });

    config.AddBranch("ui", ui =>
    {
        ui.AddCommand<UiSplashCommand>("splash").WithDescription("Render the run splash banner without starting any work.");
        ui.AddCommand<UiRunPreviewCommand>("run-preview").WithDescription("Render the exact queue slice that the run command would process.");
    });
});

return await app.RunAsync(args);
