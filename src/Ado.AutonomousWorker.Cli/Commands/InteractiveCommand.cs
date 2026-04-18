using Ado.AutonomousWorker.Cli.Interactive;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

/// <summary>
/// Launches the interactive REPL mode with slash commands,
/// tab completion, and command history.
/// </summary>
public sealed class InteractiveCommand(IServiceProvider services) : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var session = new InteractiveSession(services);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
        };

        return await session.RunAsync(cancellationToken);
    }
}
