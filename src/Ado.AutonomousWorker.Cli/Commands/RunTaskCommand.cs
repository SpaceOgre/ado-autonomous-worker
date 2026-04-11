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

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var executionId = await taskOrchestrator.RunTaskAsync(settings.WorkItemId, CancellationToken.None);
        AnsiConsole.MarkupLine($"[green]Execution started and finished.[/] Id: {executionId}");
        return 0;
    }
}
