using Ado.AutonomousWorker.Cli.Commands;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli;

/// <summary>
/// Shared Spectre.Console.Cli command configuration used by both
/// the main entry point and the interactive session sub-app.
/// </summary>
public static class CliAppConfiguration
{
    public static void Configure(IConfigurator config)
    {
        config.SetApplicationName("ado-worker");
        config
            .AddCommand<BootstrapCommand>("bootstrap")
            .WithDescription("Initialize storage and workspace directories.");
        config.AddCommand<ResetCommand>("reset").WithDescription("Delete the local worker database.");
        config
            .AddCommand<DoctorCommand>("doctor")
            .WithDescription("Verify configuration, repos, CLI tools, and Azure DevOps access.");
        config.AddCommand<QueueCommand>("queue").WithDescription("List eligible AI-Work and AI-Rework items.");
        config.AddCommand<ExecutionsCommand>("executions").WithDescription("List local task executions.");
        config
            .AddCommand<RunCommand>("run")
            .WithDescription("Preview the selected queue, then process it and stop on the first failure.");
        config
            .AddCommand<DeployCommand>("deploy")
            .WithDescription(
                "Trigger the deploy pipeline for backend and frontend, wait for success, then move sprint items to Ready for test."
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
    }
}
