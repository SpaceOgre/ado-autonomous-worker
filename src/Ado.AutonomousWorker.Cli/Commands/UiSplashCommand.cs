using System.ComponentModel;
using Ado.AutonomousWorker.Cli.Presentation;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class UiSplashCommand(RunPreambleDisplay runPreambleDisplay) : Command<UiSplashCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Optional status text shown under the splash banner.")]
        [CommandOption("--status <TEXT>")]
        public string? Status { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        runPreambleDisplay.ShowSplash(settings.Status);
        return 0;
    }
}
