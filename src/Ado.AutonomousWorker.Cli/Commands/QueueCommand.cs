using Ado.AutonomousWorker.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class QueueCommand(QueueInspector queueInspector) : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var queue = await queueInspector.GetQueueAsync(CancellationToken.None);
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Work Item", "Title", "Kind", "State", "Assigned To", "StackRank");

        foreach (var item in queue)
        {
            table.AddRow(
                item.WorkItemId.ToString(),
                item.Title,
                item.Kind.ToString(),
                item.State,
                item.AssignedTo ?? string.Empty,
                item.StackRank.ToString("0.##"));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
