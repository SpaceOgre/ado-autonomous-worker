using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class BootstrapCommand(IStateStore stateStore, IWorkspaceManager workspaceManager) : AsyncCommand
{
    private const string ConfigTemplate = """
        {
            // ado-worker user configuration.
            // Values here override the defaults shipped in appsettings.json.
            // Only settings you include will take effect; everything else uses the default.
            //
            // See appsettings.json in the tool installation directory for all available settings.

            "AzureDevOps": {
                // Required: your Azure DevOps personal access token.
                // Generate one at: {OrganizationUrl}/_userSettings/tokens
                "PersonalAccessToken": ""
            }

            // Uncomment and fill in other settings you want to override, for example:
            //
            // "AzureDevOps": {
            //     "PersonalAccessToken": "your-pat-here",
            //     "OrganizationUrl": "https://dev.azure.com/your-org",
            //     "Project": "YourProject",
            //     "Team": "YourProject Team",
            //     "DefaultAssignee": "you@example.com"
            // },
            //
            // "Repositories": {
            //     "BaseClonePath": "C:\\Workspace\\YourRepo",
            //     "WorktreeRoot": "C:\\Workspace\\YourRepo.wt"
            // },
            //
            // "Worker": { "MaxItemsPerRun": 3, "DryRun": true }
        }
        """;

    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await stateStore.InitializeAsync(cancellationToken);
        await workspaceManager.PrepareAsync(cancellationToken);

        var configPath = Path.Combine(StorageOptions.DefaultAppFolder, "config.jsonc");
        if (File.Exists(configPath))
        {
            AnsiConsole.MarkupLine("[grey]User config already exists — skipped.[/]");
        }
        else
        {
            await File.WriteAllTextAsync(configPath, ConfigTemplate, cancellationToken);
            AnsiConsole.MarkupLine($"[green]User config created:[/] {configPath}");
            AnsiConsole.MarkupLine("Open it and fill in [bold]AzureDevOps.PersonalAccessToken[/].");
        }

        AnsiConsole.MarkupLine("[green]Bootstrap complete.[/]");
        return 0;
    }
}
