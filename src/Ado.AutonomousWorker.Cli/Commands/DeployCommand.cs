using System.ComponentModel;
using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Deploy;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class DeployCommand(
    DeployService deployService,
    IOptions<DeployOptions> deployOptions,
    IStateStore stateStore,
    DeployLiveDisplay deployLiveDisplay
) : AsyncCommand<DeployCommand.Settings>
{
    private readonly DeployService _deployService = deployService;
    private readonly DeployOptions _options = deployOptions.Value;
    private readonly IStateStore _stateStore = stateStore;
    private readonly DeployLiveDisplay _deployLiveDisplay = deployLiveDisplay;

    public sealed class Settings : CommandSettings
    {
        [Description(
            "Preview the work item changes that would be made without actually running pipelines or updating anything."
        )]
        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }

        [Description("Trigger the pipeline from this git tag instead of the default branch. Example: --tag v1.2.3")]
        [CommandOption("--tag <TAG>")]
        public string? Tag { get; init; }

        [Description(
            "Only deploy the backend. Cannot be combined with --frontend unless both are specified (which is the default)."
        )]
        [CommandOption("--backend")]
        public bool BackendOnly { get; init; }

        [Description(
            "Only deploy the frontend. Cannot be combined with --backend unless both are specified (which is the default)."
        )]
        [CommandOption("--frontend")]
        public bool FrontendOnly { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        if (settings.DryRun)
        {
            return await RunDryRunAsync(cancellationToken);
        }

        // If neither flag is set, deploy both (default). If one or both flags are set, deploy only those.
        var deployBackend = !settings.BackendOnly && !settings.FrontendOnly || settings.BackendOnly;
        var deployFrontend = !settings.BackendOnly && !settings.FrontendOnly || settings.FrontendOnly;

        return await RunDeployAsync(settings.Tag, deployBackend, deployFrontend, cancellationToken);
    }

    private async Task<int> RunDryRunAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.Write(CliStyle.SectionRule("Dry Run — no changes will be made", "yellow"));
        AnsiConsole.MarkupLine("[yellow]Computing work item changes that would occur after a successful deploy…[/]");
        AnsiConsole.WriteLine();

        (IReadOnlyList<DeployWorkItemChange> taskChanges, IReadOnlyList<DeployWorkItemChange> storyChanges) changes;
        try
        {
            changes = await AnsiConsole
                .Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Querying Azure DevOps…", _ => _deployService.ComputeChangesAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Failed to compute changes: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        RenderChangesTable("Tasks / Bugs → Ready for test", changes.taskChanges);
        RenderChangesTable("User Stories → Ready for test", changes.storyChanges);

        if (changes.taskChanges.Count == 0 && changes.storyChanges.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No work items would be updated.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[yellow]Dry run complete.[/] [grey]{changes.taskChanges.Count} task/bug(s) and {changes.storyChanges.Count} user story/stories would be updated.[/]"
        );
        return 0;
    }

    private async Task<int> RunDeployAsync(
        string? tag,
        bool deployBackend,
        bool deployFrontend,
        CancellationToken cancellationToken
    )
    {
        if (!ValidateConfig())
            return 1;

        var refValue = tag ?? _options.Branch;
        var targetLabel = DeployService.DescribeTargets(deployBackend, deployFrontend);

        AnsiConsole.Write(CliStyle.SectionRule("Pipeline Configuration", "deepskyblue1"));
        var configTable = new Table().Border(TableBorder.Rounded).AddColumns("Setting", "Value");
        configTable.AddRow("Deploy Pipeline ID", _options.PipelineId.ToString());
        configTable.AddRow("Environment", Markup.Escape(_options.Environment));
        configTable.AddRow("Deploys", targetLabel);
        configTable.AddRow(
            "Trigger Ref",
            Markup.Escape(tag is not null ? $"tag: {tag}" : $"branch: {_options.Branch}")
        );
        configTable.AddRow("Ready-for-Test Assignee", Markup.Escape(_options.ReadyForTestAssignee));
        configTable.AddRow("Poll Interval", $"{_options.PollIntervalSeconds}s");
        configTable.AddRow("Timeout", $"{_options.TimeoutMinutes}m");
        AnsiConsole.Write(configTable);
        AnsiConsole.WriteLine();

        if (
            AnsiConsole.Profile.Capabilities.Interactive
            && !AnsiConsole.Confirm($"Deploy {targetLabel} to {_options.Environment} and wait for completion?")
        )
        {
            return 0;
        }

        var deployId = Guid.NewGuid().ToString("N");
        var deploy = new DeployRecord(
            DeployId: deployId,
            Status: DeployStatus.Running,
            Phase: DeployPhase.TriggeringPipelines,
            Ref: refValue,
            IsTag: tag is not null,
            // One run of the deploy pipeline covers both halves; each deployed half records that pipeline and run.
            BackendPipelineId: deployBackend ? _options.PipelineId : null,
            FrontendPipelineId: deployFrontend ? _options.PipelineId : null,
            BackendRunId: null,
            FrontendRunId: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            FailureReason: null
        );
        await _stateStore.SaveDeployAsync(deploy, cancellationToken);

        (IReadOnlyList<DeployWorkItemChange> TaskChanges, IReadOnlyList<DeployWorkItemChange> StoryChanges) changes;

        try
        {
            changes = await _deployLiveDisplay.RunAsync(
                deployId,
                async (progress, ct) =>
                {
                    // Step 1: Trigger the deploy pipeline
                    var runId = await _deployService.TriggerPipelineAsync(
                        progress,
                        ct,
                        tag,
                        deployBackend,
                        deployFrontend
                    );
                    await _stateStore.UpdateDeployRunIdsAsync(
                        deployId,
                        deployBackend ? runId : null,
                        deployFrontend ? runId : null,
                        ct
                    );
                    await _stateStore.UpdateDeployPhaseAsync(deployId, DeployPhase.WaitingForPipelines, ct);

                    // Step 2: Wait for the run
                    await _deployService.WaitForPipelineAsync(runId, progress, ct);
                    await _stateStore.UpdateDeployPhaseAsync(deployId, DeployPhase.ComputingChanges, ct);

                    // Step 3: Compute work item changes
                    var (taskChanges, storyChanges) = await _deployService.ComputeChangesAsync(ct);
                    progress.Report(
                        $"Found {taskChanges.Count} task/bug(s) and {storyChanges.Count} user story/stories to update."
                    );
                    await _stateStore.UpdateDeployPhaseAsync(deployId, DeployPhase.ApplyingChanges, ct);

                    // Step 4: Apply changes
                    if (taskChanges.Count > 0 || storyChanges.Count > 0)
                        await _deployService.ApplyChangesAsync(taskChanges, storyChanges, progress, ct);

                    return (taskChanges, storyChanges);
                },
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            await _stateStore.UpdateDeployStatusAsync(
                deployId,
                DeployStatus.Failed,
                "Cancelled.",
                CancellationToken.None
            );
            throw;
        }
        catch (Exception ex)
        {
            await _stateStore.UpdateDeployStatusAsync(
                deployId,
                DeployStatus.Failed,
                ex.Message,
                CancellationToken.None
            );
            AnsiConsole.MarkupLine($"[red]Deploy failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        var allChanges = changes.TaskChanges.Concat(changes.StoryChanges).ToArray();
        await _stateStore.SaveDeployWorkItemChangesAsync(deployId, allChanges, CancellationToken.None);
        await _stateStore.UpdateDeployStatusAsync(deployId, DeployStatus.Completed, null, CancellationToken.None);

        RenderChangesTable("Tasks / Bugs → Ready for test", changes.TaskChanges);
        RenderChangesTable("User Stories → Ready for test", changes.StoryChanges);

        if (changes.TaskChanges.Count == 0 && changes.StoryChanges.Count == 0)
            AnsiConsole.MarkupLine("[grey]No work items needed updating.[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[green]✓ Deploy complete.[/] Updated {changes.TaskChanges.Count} task/bug(s) and {changes.StoryChanges.Count} user story/stories to [deepskyblue1]Ready for test[/]."
        );
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

        var table = new Table().Border(TableBorder.Rounded).AddColumns("ID", "Type", "Title", "State", "→", "Assignee");

        foreach (var change in changes)
        {
            table.AddRow(
                $"#{change.WorkItemId}",
                change.WorkItemType,
                Markup.Escape(change.Title.Length > 60 ? change.Title[..60] + "…" : change.Title),
                CliStyle.WorkItemState(change.CurrentState),
                CliStyle.WorkItemState(change.NewState),
                Markup.Escape(change.NewAssignee)
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private bool ValidateConfig()
    {
        var errors = new List<string>();
        if (_options.PipelineId <= 0)
            errors.Add("Deploy:PipelineId is not configured.");
        if (string.IsNullOrWhiteSpace(_options.Environment))
            errors.Add("Deploy:Environment is not configured.");
        if (string.IsNullOrWhiteSpace(_options.ReadyForTestAssignee))
            errors.Add("Deploy:ReadyForTestAssignee is not configured.");

        if (errors.Count == 0)
            return true;

        foreach (var error in errors)
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
        AnsiConsole.MarkupLine(
            "[red]Deploy configuration is incomplete. Check your appsettings.local.json or config.jsonc.[/]"
        );
        return false;
    }
}
