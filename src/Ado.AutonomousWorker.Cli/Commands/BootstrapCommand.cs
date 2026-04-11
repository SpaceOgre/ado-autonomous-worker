using Ado.AutonomousWorker.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class BootstrapCommand(IStateStore stateStore, IWorkspaceManager workspaceManager) : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await stateStore.InitializeAsync(cancellationToken);
        await workspaceManager.PrepareAsync(cancellationToken);
        AnsiConsole.MarkupLine("[green]Bootstrap complete.[/]");
        return 0;
    }
}
