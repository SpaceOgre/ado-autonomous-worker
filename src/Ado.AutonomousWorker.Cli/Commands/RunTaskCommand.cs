using Ado.AutonomousWorker.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class RunTaskCommand(ITaskOrchestrator taskOrchestrator) : AsyncCommand<RunTaskCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--id <WORK_ITEM_ID>")]
        public required int WorkItemId { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var executionId = await taskOrchestrator.RunTaskAsync(settings.WorkItemId, cancellationToken);
        AnsiConsole.MarkupLine($"[green]Execution started and finished.[/] Id: {executionId}");
        return 0;
    }
}
