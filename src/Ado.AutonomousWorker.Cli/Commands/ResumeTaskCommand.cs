using Ado.AutonomousWorker.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class ResumeTaskCommand(ITaskOrchestrator taskOrchestrator) : AsyncCommand<ResumeTaskCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--execution-id <EXECUTION_ID>")]
        public required string ExecutionId { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        await taskOrchestrator.ResumeTaskAsync(settings.ExecutionId, CancellationToken.None);
        AnsiConsole.MarkupLine($"[green]Execution resumed.[/] Id: {settings.ExecutionId}");
        return 0;
    }
}
