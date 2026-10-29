using System.ComponentModel;
using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class ResumeTaskCommand(
    ITaskOrchestrator taskOrchestrator,
    IStateStore stateStore,
    ExecutionLiveDisplay liveDisplay
) : AsyncCommand<ResumeTaskCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("The local execution id to resume from persisted state.")]
        [CommandOption("--execution-id <EXECUTION_ID>")]
        public string? ExecutionId { get; init; }

        [Description("Resume the most recently updated local execution.")]
        [CommandOption("--last")]
        public bool Last { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var hasExecutionId = !string.IsNullOrWhiteSpace(settings.ExecutionId);
        if (settings.Last == hasExecutionId)
        {
            AnsiConsole.MarkupLine(
                "[red]Specify exactly one of:[/] [yellow]--execution-id <execution-id>[/] or [yellow]--last[/]"
            );
            return 1;
        }

        var executionId = settings.ExecutionId;
        if (settings.Last)
        {
            await stateStore.InitializeAsync(cancellationToken);
            executionId = (await stateStore.ListExecutionsAsync(cancellationToken)).FirstOrDefault()?.ExecutionId;
            if (string.IsNullOrWhiteSpace(executionId))
            {
                AnsiConsole.MarkupLine("[yellow]No local executions available to resume.[/]");
                return 0;
            }
        }

        await liveDisplay.RunForExecutionAsync(
            executionId!,
            token => taskOrchestrator.ResumeTaskAsync(executionId!, token),
            cancellationToken
        );

        AnsiConsole.MarkupLine($"[green]Execution finished.[/] Id: {executionId}");
        return 0;
    }
}
