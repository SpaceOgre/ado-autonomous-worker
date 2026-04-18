using System.ComponentModel;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Enums;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class SetPhaseCommand(IStateStore stateStore) : AsyncCommand<SetPhaseCommand.Settings>
{
    private static readonly TaskPhase[] AllowedPhases =
    [
        TaskPhase.Reviewing,
        TaskPhase.ApplyingReview,
        TaskPhase.Validating,
        TaskPhase.Publishing,
    ];

    public sealed class Settings : CommandSettings
    {
        [Description("The local execution id to update.")]
        [CommandOption("--execution-id <EXECUTION_ID>")]
        public string? ExecutionId { get; init; }

        [Description("Update the most recently updated local execution.")]
        [CommandOption("--last")]
        public bool Last { get; init; }

        [Description("Phase to rewind to: Reviewing, ApplyingReview, Validating, or Publishing.")]
        [CommandOption("--phase <PHASE>")]
        public string? Phase { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var hasExecutionId = !string.IsNullOrWhiteSpace(settings.ExecutionId);
        if (settings.Last == hasExecutionId)
        {
            AnsiConsole.MarkupLine(
                "[red]Specify exactly one of:[/] [yellow]--execution-id <id>[/] or [yellow]--last[/]"
            );
            return 1;
        }

        if (
            string.IsNullOrWhiteSpace(settings.Phase)
            || !Enum.TryParse<TaskPhase>(settings.Phase, ignoreCase: true, out var targetPhase)
            || !AllowedPhases.Contains(targetPhase)
        )
        {
            var valid = string.Join(", ", AllowedPhases.Select(p => p.ToString()));
            AnsiConsole.MarkupLine($"[red]--phase must be one of:[/] {valid}");
            return 1;
        }

        await stateStore.InitializeAsync(cancellationToken);

        var executionId = settings.ExecutionId;
        if (settings.Last)
        {
            executionId = (await stateStore.ListExecutionsAsync(cancellationToken)).FirstOrDefault()?.ExecutionId;
            if (string.IsNullOrWhiteSpace(executionId))
            {
                AnsiConsole.MarkupLine("[yellow]No local executions found.[/]");
                return 0;
            }
        }

        var execution = await stateStore.GetExecutionAsync(executionId!, cancellationToken);
        if (execution is null)
        {
            AnsiConsole.MarkupLine($"[red]Execution '{executionId}' was not found.[/]");
            return 1;
        }

        await stateStore.UpdatePhaseAsync(executionId!, targetPhase, cancellationToken);
        await stateStore.UpdateStatusAsync(executionId!, ExecutionStatus.Failed, cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]✓[/] Execution [grey]{executionId}[/] reset to "
                + $"Phase=[deepskyblue1]{targetPhase}[/] Status=[red]Failed[/]. "
                + $"Run [yellow]task resume --execution-id {executionId}[/] to continue."
        );

        return 0;
    }
}
