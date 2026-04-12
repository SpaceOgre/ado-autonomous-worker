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
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Work Item", "Type", "Title", "Kind", "State", "Assigned To", "StackRank", "Priority");

        foreach (var item in queue)
        {
            table.AddRow(
                item.WorkItemId.ToString(),
                CliStyle.WorkItemTypeLabel(item.WorkItemType),
                Markup.Escape(item.Title),
                CliStyle.QueueKind(item.Kind),
                CliStyle.WorkItemState(item.State),
                string.IsNullOrWhiteSpace(item.AssignedTo) ? "[grey]-[/]" : Markup.Escape(item.AssignedTo),
                FormatStackRank(item.StackRank),
                FormatPriority(item.Priority));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static string FormatStackRank(double stackRank)
    {
        return stackRank >= double.MaxValue / 2
            ? "-"
            : stackRank.ToString("0.##");
    }

    private static string FormatPriority(int? priority)
    {
        return CliStyle.Priority(priority);
    }
}
