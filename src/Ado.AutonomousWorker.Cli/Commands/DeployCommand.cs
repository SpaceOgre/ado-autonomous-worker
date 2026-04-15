using System.ComponentModel;
using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Deploy;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class DeployCommand(
    DeployService deployService,
    IOptions<DeployOptions> deployOptions) : AsyncCommand<DeployCommand.Settings>
{
    private readonly DeployService _deployService = deployService;
    private readonly DeployOptions _options = deployOptions.Value;

    public sealed class Settings : CommandSettings
    {
        [Description("Preview the work item changes that would be made without actually running pipelines or updating anything.")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        RunPreambleDisplay.ShowSplash("Deploy");

        if (settings.DryRun)
        {
            return await RunDryRunAsync(cancellationToken);
        }

        return await RunDeployAsync(cancellationToken);
    }

    private async Task<int> RunDryRunAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.Write(CliStyle.SectionRule("Dry Run — no changes will be made", "yellow"));
        AnsiConsole.MarkupLine("[yellow]Computing work item changes that would occur after a successful deploy…[/]");
        AnsiConsole.WriteLine();

        (IReadOnlyList<DeployWorkItemChange> taskChanges, IReadOnlyList<DeployWorkItemChange> storyChanges) changes;
        try
        {
            changes = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(
                    "Querying Azure DevOps…",
                    _ => _deployService.ComputeChangesAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Failed to compute changes: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        RenderChangesTable("Tasks / Bugs → Ready for Test", changes.taskChanges);
        RenderChangesTable("User Stories → Ready for Test", changes.storyChanges);

        if (changes.taskChanges.Count == 0 && changes.storyChanges.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No work items would be updated.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]Dry run complete.[/] [grey]{changes.taskChanges.Count} task/bug(s) and {changes.storyChanges.Count} user story/stories would be updated.[/]");
        return 0;
    }

    private async Task<int> RunDeployAsync(CancellationToken cancellationToken)
    {
        ValidateConfig();

        AnsiConsole.Write(CliStyle.SectionRule("Pipeline Configuration", "deepskyblue1"));
        var configTable = new Table().Border(TableBorder.Rounded)
            .AddColumns("Setting", "Value");
        configTable.AddRow("Backend Pipeline ID", _options.BackendPipelineId.ToString());
        configTable.AddRow("Frontend Pipeline ID", _options.FrontendPipelineId.ToString());
        configTable.AddRow("Branch", Markup.Escape(_options.Branch));
        configTable.AddRow("Ready-for-Test Assignee", Markup.Escape(_options.ReadyForTestAssignee));
        configTable.AddRow("Poll Interval", $"{_options.PollIntervalSeconds}s");
        configTable.AddRow("Timeout", $"{_options.TimeoutMinutes}m");
        AnsiConsole.Write(configTable);
        AnsiConsole.WriteLine();

        if (AnsiConsole.Profile.Capabilities.Interactive &&
            !AnsiConsole.Confirm("Trigger both pipelines and wait for completion?"))
        {
            return 0;
        }

        // ── Step 1: Trigger pipelines ─────────────────────────────────────
        AnsiConsole.Write(CliStyle.SectionRule("Triggering Pipelines", "deepskyblue1"));

        int backendRunId;
        int frontendRunId;

        try
        {
            var progressMessages = new List<string>();
            var progress = new Progress<string>(msg =>
            {
                AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}");
                progressMessages.Add(msg);
            });

            (backendRunId, frontendRunId) = await _deployService.TriggerPipelinesAsync(progress, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Failed to trigger pipelines: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        AnsiConsole.WriteLine();

        // ── Step 2: Wait for pipelines ────────────────────────────────────
        AnsiConsole.Write(CliStyle.SectionRule("Waiting for Pipelines", "deepskyblue1"));
        AnsiConsole.MarkupLine($"[grey]Polling every {_options.PollIntervalSeconds}s (timeout {_options.TimeoutMinutes}m)…[/]");
        AnsiConsole.WriteLine();

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(
                    "Pipelines running…",
                    async ctx =>
                    {
                        var progress = new Progress<string>(msg =>
                        {
                            ctx.Status(Markup.Escape(msg));
                            AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}");
                        });
                        await _deployService.WaitForPipelinesAsync(backendRunId, frontendRunId, progress, cancellationToken);
                    });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Pipeline failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]✓ Both pipelines completed successfully.[/]");
        AnsiConsole.WriteLine();

        // ── Step 3: Compute work item changes ─────────────────────────────
        AnsiConsole.Write(CliStyle.SectionRule("Computing Work Item Changes", "deepskyblue1"));

        (IReadOnlyList<DeployWorkItemChange> taskChanges, IReadOnlyList<DeployWorkItemChange> storyChanges) changes;
        try
        {
            changes = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(
                    "Querying Azure DevOps…",
                    _ => _deployService.ComputeChangesAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Failed to compute work item changes: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        RenderChangesTable("Tasks / Bugs → Ready for Test", changes.taskChanges);
        RenderChangesTable("User Stories → Ready for Test", changes.storyChanges);

        if (changes.taskChanges.Count == 0 && changes.storyChanges.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No work items need updating.[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]Deploy complete.[/]");
            return 0;
        }

        AnsiConsole.WriteLine();

        // ── Step 4: Apply changes ─────────────────────────────────────────
        AnsiConsole.Write(CliStyle.SectionRule("Updating Work Items", "deepskyblue1"));

        try
        {
            var progress = new Progress<string>(msg =>
                AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}"));

            await _deployService.ApplyChangesAsync(changes.taskChanges, changes.storyChanges, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Failed to update work items: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓ Deploy complete.[/] Updated {changes.taskChanges.Count} task/bug(s) and {changes.storyChanges.Count} user story/stories to [deepskyblue1]Ready for Test[/].");
        return 0;
    }

    private static void RenderChangesTable(string title, IReadOnlyList<DeployWorkItemChange> changes)
    {
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(title)}[/] [grey]({changes.Count})[/]");

        if (changes.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]  (none)[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumns("ID", "Type", "Title", "State", "→", "Assignee");

        foreach (var change in changes)
        {
            table.AddRow(
                $"#{change.WorkItemId}",
                change.WorkItemType,
                Markup.Escape(change.Title.Length > 60 ? change.Title[..60] + "…" : change.Title),
                CliStyle.WorkItemState(change.CurrentState),
                CliStyle.WorkItemState(change.NewState),
                Markup.Escape(change.NewAssignee));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private void ValidateConfig()
    {
        var errors = new List<string>();
        if (_options.BackendPipelineId <= 0)
            errors.Add("Deploy:BackendPipelineId is not configured.");
        if (_options.FrontendPipelineId <= 0)
            errors.Add("Deploy:FrontendPipelineId is not configured.");
        if (string.IsNullOrWhiteSpace(_options.ReadyForTestAssignee))
            errors.Add("Deploy:ReadyForTestAssignee is not configured.");

        if (errors.Count > 0)
        {
            foreach (var error in errors)
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            throw new InvalidOperationException("Deploy configuration is incomplete. Check your appsettings.local.json or config.jsonc.");
        }
    }
}
