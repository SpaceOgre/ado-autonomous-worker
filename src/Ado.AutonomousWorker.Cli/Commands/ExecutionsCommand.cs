using System.ComponentModel;
using Ado.AutonomousWorker.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class ExecutionsCommand(IStateStore stateStore) : AsyncCommand<ExecutionsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Show only executions that are still in progress and not completed, failed, or skipped.")]
        [CommandOption("--active")]
        public bool ActiveOnly { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await stateStore.InitializeAsync(cancellationToken);

        var executions = settings.ActiveOnly
            ? await stateStore.ListActiveExecutionsAsync(cancellationToken)
            : await stateStore.ListExecutionsAsync(cancellationToken);

        if (executions.Count == 0)
        {
            AnsiConsole.MarkupLine(settings.ActiveOnly
                ? "[yellow]No active executions found.[/]"
                : "[yellow]No local executions found.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Execution Id", "Work Item", "Kind", "Phase", "Updated", "Title");
        foreach (var execution in executions)
        {
            table.AddRow(
                execution.ExecutionId,
                execution.WorkItemId.ToString(),
                execution.Kind.ToString(),
                execution.Phase.ToString(),
                execution.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Markup.Escape(execution.Title));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
