using Ado.AutonomousWorker.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class RunCommand(ITaskOrchestrator taskOrchestrator) : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[blue]Processing queue...[/]");
        var executionIds = await taskOrchestrator.RunQueueAsync(cancellationToken);

        AnsiConsole.MarkupLine($"[green]Completed queue run.[/] Executions: {string.Join(", ", executionIds)}");
        return 0;
    }
}
