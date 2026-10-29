using System.ComponentModel;
using Ado.AutonomousWorker.Core.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class AzureDevOpsCommentTestCommand(IAzureDevOpsClient azureDevOpsClient)
    : AsyncCommand<AzureDevOpsCommentTestCommand.Settings>
{
    private const string TestComment = """
        This is a **markdown test comment** from `ado-worker`.

        ### Backend

        Task: #12345
        Branch: `feature/12345-test-branch`
        Commit: `ab12cd34`
        PR: !999
        Validation: `dotnet build | dotnet test`

        ### Review

        3 findings, 2 accepted.
        The implementation looks correct. Minor style issues noted in the findings.
        """;

    public sealed class Settings : CommandSettings
    {
        [Description("Work item ID to post the test comment on.")]
        [CommandOption("--work-item <ID>")]
        public int WorkItemId { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        if (settings.WorkItemId <= 0)
        {
            AnsiConsole.MarkupLine("[red]--work-item is required.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"Posting markdown test comment to work item [deepskyblue1]#{settings.WorkItemId}[/]..."
        );
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Comment content:[/]");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(TestComment)}[/]");
        AnsiConsole.WriteLine();

        try
        {
            await azureDevOpsClient.AddCommentAsync(settings.WorkItemId, TestComment, cancellationToken);
            AnsiConsole.MarkupLine(
                "[green]✓[/] Comment posted. Check the work item in Azure DevOps to verify markdown rendered correctly."
            );
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
