using Ado.AutonomousWorker.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class BootstrapCommand(IStateStore stateStore, IWorkspaceManager workspaceManager) : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        await stateStore.InitializeAsync(CancellationToken.None);
        await workspaceManager.PrepareAsync(CancellationToken.None);
        AnsiConsole.MarkupLine("[green]Bootstrap complete.[/]");
        return 0;
    }
}
