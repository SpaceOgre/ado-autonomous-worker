using Ado.AutonomousWorker.Cli.Interactive;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

/// <summary>
/// Launches the interactive REPL mode with slash commands,
/// tab completion, and command history.
/// </summary>
public sealed class InteractiveCommand(IServiceProvider services) : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var session = new InteractiveSession(services);
        return await session.RunAsync(cancellationToken);
    }
}
