using Ado.AutonomousWorker.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class RunCommand(ITaskOrchestrator taskOrchestrator) : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var executionIds = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Star)
            .StartAsync("Processing queue...", _ => taskOrchestrator.RunQueueAsync(CancellationToken.None));

        AnsiConsole.MarkupLine($"[green]Completed queue run.[/] Executions: {string.Join(", ", executionIds)}");
        return 0;
    }
}
