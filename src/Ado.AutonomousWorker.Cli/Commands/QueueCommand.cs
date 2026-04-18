using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Infrastructure.Queue;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class QueueCommand(QueueInspector queueInspector) : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var queue = await queueInspector.GetQueueAsync(cancellationToken);
        var table = QueueTableRenderer.CreateTable(queue, includeOrder: false);
        AnsiConsole.Write(table);
        return 0;
    }
}
