using System.ComponentModel;
using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class RunTaskCommand(ITaskOrchestrator taskOrchestrator, ExecutionLiveDisplay liveDisplay)
    : AsyncCommand<RunTaskCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("The Azure DevOps work item id to execute immediately.")]
        [CommandOption("--id <WORK_ITEM_ID>")]
        public int? WorkItemId { get; init; }

        [Description("The pull request id to run as an AiPrFix execution (work item resolved automatically).")]
        [CommandOption("--pr <PULL_REQUEST_ID>")]
        public int? PullRequestId { get; init; }

        [Description(
            "Override the iteration path used for backlog items (e.g. 'MyProject\\Sprint 5'). Has no effect on items already assigned to a sprint."
        )]
        [CommandOption("--iteration <ITERATION_PATH>")]
        public string? Iteration { get; init; }

        public override ValidationResult Validate()
        {
            if (WorkItemId is null && PullRequestId is null)
                return ValidationResult.Error("Specify either --id <WORK_ITEM_ID> or --pr <PULL_REQUEST_ID>.");
            if (WorkItemId is not null && PullRequestId is not null)
                return ValidationResult.Error("--id and --pr are mutually exclusive.");
            return ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        using var wakeLock = new WakeLock();
        string executionId;

        if (settings.PullRequestId is not null)
        {
            executionId = await liveDisplay.RunForWorkItemAsync(
                $"PR fix !{settings.PullRequestId}",
                workItemId: 0,
                token => taskOrchestrator.RunPrFixAsync(settings.PullRequestId.Value, token),
                cancellationToken
            );
        }
        else
        {
            executionId = await liveDisplay.RunForWorkItemAsync(
                settings.WorkItemId!.Value,
                token => taskOrchestrator.RunTaskAsync(settings.WorkItemId.Value, settings.Iteration, token),
                cancellationToken
            );
        }

        AnsiConsole.MarkupLine($"[green]Execution finished.[/] Id: {executionId}");
        return 0;
    }
}
