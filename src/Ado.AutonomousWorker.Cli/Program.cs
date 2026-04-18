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
builder.Services.AddSingleton<DashboardCommand>();
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

var registrar = new TypeRegistrar(host.Services);
var app = new CommandApp(registrar);
app.Configure(config =>
{
    config.SetApplicationName("ado-worker");
    config.AddCommand<BootstrapCommand>("bootstrap").WithDescription("Initialize storage and workspace directories.");
    config.AddCommand<ResetCommand>("reset").WithDescription("Delete the local worker database.");
    config
        .AddCommand<DoctorCommand>("doctor")
        .WithDescription("Verify configuration, repos, CLI tools, and Azure DevOps access.");
    config.AddCommand<QueueCommand>("queue").WithDescription("List eligible AI-Work and AI-Rework items.");
    config.AddCommand<ExecutionsCommand>("executions").WithDescription("List local task executions.");
    config
        .AddCommand<DashboardCommand>("dashboard")
        .WithDescription("Start the local Blazor web dashboard and open it in the browser.");
    config
        .AddCommand<RunCommand>("run")
        .WithDescription("Preview the selected queue, then process it and stop on the first failure.");
    config
        .AddCommand<DeployCommand>("deploy")
        .WithDescription(
            "Trigger frontend and backend pipelines, wait for success, then move sprint items to Ready for test."
        );
    config
        .AddCommand<VersionCommand>("version")
        .WithDescription("Show the installed ado-worker version and runtime information.");
    config.AddBranch(
        "completion",
        completion =>
        {
            completion
                .AddCommand<PowerShellCompletionCommand>("powershell")
                .WithDescription("Print a PowerShell tab-completion registration script.");
        }
    );

    config.AddBranch(
        "execution",
        execution =>
        {
            execution
                .AddCommand<ShowExecutionCommand>("show")
                .WithDescription("Show one local execution with events and agent sessions.");
        }
    );

    config.AddBranch(
        "task",
        task =>
        {
            task.AddCommand<RunTaskCommand>("run").WithDescription("Run a specific Azure DevOps work item.");
            task.AddCommand<ResumeTaskCommand>("resume").WithDescription("Resume a prior execution by ID.");
            task.AddCommand<SetPhaseCommand>("set-phase")
                .WithDescription("Rewind an execution to an earlier phase so it can be resumed from there.");
        }
    );

    config.AddBranch(
        "agent",
        agent =>
        {
            agent
                .AddCommand<AgentTestCommand>("test")
                .WithDescription("Send a test prompt to each configured agent to verify connectivity.");
        }
    );

    config.AddBranch(
        "ado",
        ado =>
        {
            ado.AddCommand<AzureDevOpsCommentTestCommand>("comment-test")
                .WithDescription("Post a sample markdown comment to a work item to verify rendering.");
        }
    );

    config.AddBranch(
        "ui",
        ui =>
        {
            ui.AddCommand<UiSplashCommand>("splash")
                .WithDescription("Render the run splash banner without starting any work.");
            ui.AddCommand<UiRunPreviewCommand>("run-preview")
                .WithDescription("Render the exact queue slice that the run command would process.");
        }
    );
});

return await app.RunAsync(args);
