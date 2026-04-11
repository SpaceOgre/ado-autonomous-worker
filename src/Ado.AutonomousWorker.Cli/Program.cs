using Ado.AutonomousWorker.Infrastructure;
using Ado.AutonomousWorker.Cli.Commands;
using Ado.AutonomousWorker.Cli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("AdoWorker_");

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddAutonomousWorker(builder.Configuration);
builder.Services.AddSingleton<BootstrapCommand>();
builder.Services.AddSingleton<DoctorCommand>();
builder.Services.AddSingleton<QueueCommand>();
builder.Services.AddSingleton<ExecutionsCommand>();
builder.Services.AddSingleton<ShowExecutionCommand>();
builder.Services.AddSingleton<RunCommand>();
builder.Services.AddSingleton<RunTaskCommand>();
builder.Services.AddSingleton<ResumeTaskCommand>();

using var host = builder.Build();

var registrar = new TypeRegistrar(host.Services);
var app = new CommandApp(registrar);
app.Configure(config =>
{
    config.SetApplicationName("ado-worker");
    config.AddCommand<BootstrapCommand>("bootstrap").WithDescription("Initialize storage and workspace directories.");
    config.AddCommand<DoctorCommand>("doctor").WithDescription("Verify configuration, repos, CLI tools, and Azure DevOps access.");
    config.AddCommand<QueueCommand>("queue").WithDescription("List eligible AI-Work and AI-Rework items.");
    config.AddCommand<ExecutionsCommand>("executions").WithDescription("List local task executions.");
    config.AddCommand<RunCommand>("run").WithDescription("Process the current queue.");

    config.AddBranch("execution", execution =>
    {
        execution.AddCommand<ShowExecutionCommand>("show").WithDescription("Show one local execution with events and agent sessions.");
    });

    config.AddBranch("task", task =>
    {
        task.AddCommand<RunTaskCommand>("run").WithDescription("Run a specific Azure DevOps work item.");
        task.AddCommand<ResumeTaskCommand>("resume").WithDescription("Resume a prior execution by ID.");
    });
});

return await app.RunAsync(args);
