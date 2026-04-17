using System.ComponentModel;
using Ado.AutonomousWorker.Core.Configuration;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class ResetCommand(IOptions<StorageOptions> storageOptions) : AsyncCommand<ResetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Delete without prompting for confirmation.")]
        [CommandOption("--force")]
        public bool Force { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var dbPath = storageOptions.Value.DatabasePath;

        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine("[yellow]No database found.[/]");
            return 0;
        }

        if (!settings.Force)
        {
            var confirmed = AnsiConsole.Confirm(
                $"[red]Delete[/] the worker database at [grey]{Markup.Escape(dbPath)}[/]? This cannot be undone."
            );
            if (!confirmed)
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return 0;
            }
        }

        File.Delete(dbPath);
        AnsiConsole.MarkupLine("[green]Database deleted.[/] Run [grey]bootstrap[/] or any command to recreate it.");
        return await Task.FromResult(0);
    }
}
