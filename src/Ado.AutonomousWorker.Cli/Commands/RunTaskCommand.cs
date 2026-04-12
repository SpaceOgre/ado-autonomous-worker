using System.ComponentModel;
using Ado.AutonomousWorker.Cli.Services;
using Ado.AutonomousWorker.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class RunTaskCommand(ITaskOrchestrator taskOrchestrator, ExecutionLiveDisplay liveDisplay) : AsyncCommand<RunTaskCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("The Azure DevOps work item id to execute immediately.")]
        [CommandOption("--id <WORK_ITEM_ID>")]
        public required int WorkItemId { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var executionId = await liveDisplay.RunForWorkItemAsync(
            settings.WorkItemId,
            token => taskOrchestrator.RunTaskAsync(settings.WorkItemId, token),
            cancellationToken);

        AnsiConsole.MarkupLine($"[green]Execution finished.[/] Id: {executionId}");
        return 0;
    }
}
