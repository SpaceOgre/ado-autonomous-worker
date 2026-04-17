using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Spectre.Console;

namespace Ado.AutonomousWorker.Cli.Presentation;

public static class QueueTableRenderer
{
    public static Table CreateTable(IReadOnlyList<QueueItem> items, bool includeOrder)
    {
        var table = new Table().Border(TableBorder.Rounded);
        if (includeOrder)
        {
            table.AddColumn("#");
        }

        table.AddColumns(
            "Work Item",
            "Type",
            "Title",
            "Kind",
            "Repos",
            "State",
            "Assigned To",
            "StackRank",
            "Priority"
        );

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var cells = new List<string>();
            if (includeOrder)
            {
                cells.Add((index + 1).ToString());
            }

            cells.Add(item.WorkItemId.ToString());
            cells.Add(CliStyle.WorkItemTypeLabel(item.WorkItemType));
            cells.Add(Markup.Escape(item.Title));
            cells.Add(CliStyle.QueueKind(item.Kind));
            cells.Add(FormatScope(item.Scope));
            cells.Add(CliStyle.WorkItemState(item.State));
            cells.Add(FormatAssignedTo(item.AssignedTo));
            cells.Add(CliStyle.StackRank(item.StackRank));
            cells.Add(CliStyle.Priority(item.Priority));

            table.AddRow(cells.ToArray());
        }

        return table;
    }

    private static string FormatAssignedTo(string? assignedTo)
    {
        return string.IsNullOrWhiteSpace(assignedTo) ? "[grey]-[/]" : Markup.Escape(assignedTo);
    }

    private static string FormatScope(WorkItemScope scope) =>
        scope switch
        {
            WorkItemScope.Backend => "[grey]BE[/]",
            WorkItemScope.Frontend => "[grey]FE[/]",
            _ => "[grey]BE+FE[/]",
        };
}
