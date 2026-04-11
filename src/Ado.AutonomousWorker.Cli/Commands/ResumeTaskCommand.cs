using Ado.AutonomousWorker.Core.Services;
using Ado.AutonomousWorker.Cli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class ResumeTaskCommand(ITaskOrchestrator taskOrchestrator, ExecutionLiveDisplay liveDisplay) : AsyncCommand<ResumeTaskCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--execution-id <EXECUTION_ID>")]
        public required string ExecutionId { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await liveDisplay.RunForExecutionAsync(
            settings.ExecutionId,
            token => taskOrchestrator.ResumeTaskAsync(settings.ExecutionId, token),
            cancellationToken);

        AnsiConsole.MarkupLine($"[green]Execution finished.[/] Id: {settings.ExecutionId}");
        return 0;
    }
}
