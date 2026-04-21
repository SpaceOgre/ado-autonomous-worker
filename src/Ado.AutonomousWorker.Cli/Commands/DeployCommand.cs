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

        [Description("Trigger the pipelines from this git tag instead of the default branch. Example: --tag v1.2.3")]
        [CommandOption("--tag <TAG>")]
        public string? Tag { get; init; }

        [Description(
            "Only trigger the backend pipeline. Cannot be combined with --frontend unless both are specified (which is the default)."
        )]
        [CommandOption("--backend")]
        public bool BackendOnly { get; init; }

        [Description(
            "Only trigger the frontend pipeline. Cannot be combined with --backend unless both are specified (which is the default)."
        )]
        [CommandOption("--frontend")]
        public bool FrontendOnly { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        if (settings.DryRun)
        {
            return await RunDryRunAsync(cancellationToken);
        }

        // If neither flag is set, trigger both (default). If one or both flags are set, trigger only those.
        var triggerBackend = !settings.BackendOnly && !settings.FrontendOnly || settings.BackendOnly;
        var triggerFrontend = !settings.BackendOnly && !settings.FrontendOnly || settings.FrontendOnly;

        return await RunDeployAsync(settings.Tag, triggerBackend, triggerFrontend, cancellationToken);
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
        bool triggerBackend,
        bool triggerFrontend,
        CancellationToken cancellationToken
    )
    {
        if (!ValidateConfig(triggerBackend, triggerFrontend))
            return 1;

        var refValue = tag ?? _options.Branch;
        var pipelineLabel = (triggerBackend, triggerFrontend) switch
        {
            (true, true) => "both pipelines",
            (true, false) => "backend pipeline only",
            (false, true) => "frontend pipeline only",
            _ => "no pipelines",
        };

        AnsiConsole.Write(CliStyle.SectionRule("Pipeline Configuration", "deepskyblue1"));
        var configTable = new Table().Border(TableBorder.Rounded).AddColumns("Setting", "Value");
        if (triggerBackend)
            configTable.AddRow("Backend Pipeline ID", _options.BackendPipelineId.ToString());
        if (triggerFrontend)
            configTable.AddRow("Frontend Pipeline ID", _options.FrontendPipelineId.ToString());
        configTable.AddRow("Trigger Ref", Markup.Escape(tag is not null ? $"tag: {tag}" : $"branch: {_options.Branch}"));
        configTable.AddRow("Ready-for-Test Assignee", Markup.Escape(_options.ReadyForTestAssignee));
        configTable.AddRow("Poll Interval", $"{_options.PollIntervalSeconds}s");
        configTable.AddRow("Timeout", $"{_options.TimeoutMinutes}m");
        AnsiConsole.Write(configTable);
        AnsiConsole.WriteLine();

        if (
            AnsiConsole.Profile.Capabilities.Interactive
            && !AnsiConsole.Confirm($"Trigger {pipelineLabel} and wait for completion?")
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
            BackendPipelineId: triggerBackend ? (int?)_options.BackendPipelineId : null,
            FrontendPipelineId: triggerFrontend ? (int?)_options.FrontendPipelineId : null,
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
                    // Step 1: Trigger pipelines
                    var (backendRunId, frontendRunId) = await _deployService.TriggerPipelinesAsync(
                        progress, ct, tag, triggerBackend, triggerFrontend
                    );
                    await _stateStore.UpdateDeployRunIdsAsync(deployId, backendRunId, frontendRunId, ct);
                    await _stateStore.UpdateDeployPhaseAsync(deployId, DeployPhase.WaitingForPipelines, ct);

                    // Step 2: Wait for pipelines
                    await _deployService.WaitForPipelinesAsync(backendRunId, frontendRunId, progress, ct);
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
            await _stateStore.UpdateDeployStatusAsync(deployId, DeployStatus.Failed, "Cancelled.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await _stateStore.UpdateDeployStatusAsync(deployId, DeployStatus.Failed, ex.Message, CancellationToken.None);
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

    private bool ValidateConfig(bool triggerBackend, bool triggerFrontend)
    {
        var errors = new List<string>();
        if (triggerBackend && _options.BackendPipelineId <= 0)
            errors.Add("Deploy:BackendPipelineId is not configured.");
        if (triggerFrontend && _options.FrontendPipelineId <= 0)
            errors.Add("Deploy:FrontendPipelineId is not configured.");
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
