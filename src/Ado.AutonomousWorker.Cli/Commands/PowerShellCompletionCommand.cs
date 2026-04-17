using System.ComponentModel;
using Ado.AutonomousWorker.Cli.Completion;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class PowerShellCompletionCommand(CliCompletionService completionService)
    : AsyncCommand<PowerShellCompletionCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("The command name to generate completion for. Defaults to 'ado-worker'.")]
        [CommandOption("--command-name <COMMAND_NAME>")]
        public string CommandName { get; init; } = "ado-worker";
    }

    protected override Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        AnsiConsole.WriteLine(completionService.BuildPowerShellScript(settings.CommandName));
        return Task.FromResult(0);
    }
}
