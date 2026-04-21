using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Ado.AutonomousWorker.Cli.Presentation;

public sealed class DeployLiveDisplay(IStateStore stateStore, TerminalTitleService terminalTitleService)
{
    private static readonly string[] UnicodeSpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] AsciiSpinnerFrames = ["-", "\\", "|", "/"];

    private static readonly DeployPhase[] PhaseSequence =
    [
        DeployPhase.TriggeringPipelines,
        DeployPhase.WaitingForPipelines,
        DeployPhase.ComputingChanges,
        DeployPhase.ApplyingChanges,
    ];

    private sealed record ViewData(DeployRecord Deploy, IReadOnlyList<DeployEventRecord> Events);

    public async Task<T> RunAsync<T>(
        string deployId,
        Func<IProgress<string>, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken
    )
    {
        using var titleScope = terminalTitleService.BeginScope();
        var useUnicode = AnsiConsole.Profile.Capabilities.Unicode;
        var startedAt = DateTimeOffset.UtcNow;
        var tick = 0;

        terminalTitleService.UpdateTitle(TerminalTitleFormatter.BuildWaitingTitle("Deploy", tick++, useUnicode));

        // Non-interactive: just run with plain console output.
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            var plainProgress = new Progress<string>(msg =>
                AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}"));
            return await operation(plainProgress, cancellationToken);
        }

        // Interactive: persist events to state store so the live panel can show them.
        var eventProgress = new Progress<string>(msg =>
            _ = AppendEventAsync(deployId, msg));

        var operationTask = operation(eventProgress, cancellationToken);

        const int SpinnerMs = 100;
        const int DataRefreshMs = 750;
        const int RefreshEvery = DataRefreshMs / SpinnerMs;

        var loopIteration = 0;
        var cached = await FetchAsync(deployId, cancellationToken);
        if (cached is not null)
            loopIteration = 1;

        ViewData? finalData = null;

        await AnsiConsole
            .Live(BuildView(deployId, cached, tick, startedAt, useUnicode))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Visible)
            .StartAsync(async liveCtx =>
            {
                while (!operationTask.IsCompleted)
                {
                    if (loopIteration % RefreshEvery == 0)
                        cached = await FetchAsync(deployId, cancellationToken) ?? cached;

                    terminalTitleService.UpdateTitle(BuildTitle(cached?.Deploy, tick, useUnicode));
                    liveCtx.UpdateTarget(BuildView(deployId, cached, tick, startedAt, useUnicode));
                    tick++;
                    loopIteration++;
                    await Task.Delay(SpinnerMs, cancellationToken);
                }

                finalData = await FetchAsync(deployId, cancellationToken);
                terminalTitleService.UpdateTitle(BuildTitle(finalData?.Deploy, tick, useUnicode));
                liveCtx.UpdateTarget(BuildView(deployId, finalData, tick, startedAt, useUnicode));
            });

        return await operationTask;
    }

    private async Task<ViewData?> FetchAsync(string deployId, CancellationToken cancellationToken)
    {
        var deploy = await stateStore.GetDeployAsync(deployId, cancellationToken);
        if (deploy is null) return null;
        var events = await stateStore.GetDeployEventsAsync(deployId, cancellationToken);
        return new ViewData(deploy, events);
    }

    private async Task AppendEventAsync(string deployId, string message)
    {
        try
        {
            await stateStore.AppendDeployEventAsync(deployId, message, CancellationToken.None);
        }
        catch
        {
            // Event append failures are non-fatal — the event just won't appear in the live display.
        }
    }

    private IRenderable BuildView(string deployId, ViewData? data, int tick, DateTimeOffset startedAt, bool useUnicode)
    {
        var shortId = deployId.Length > 8 ? deployId[..8] + "…" : deployId;
        IRenderable header;
        IReadOnlyList<DeployEventRecord> events = [];

        if (data is not null)
        {
            header = CreateHeader(data.Deploy, tick, startedAt, useUnicode);
            events = data.Events;
        }
        else
        {
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            var elapsedText = FormatElapsed(elapsed);
            header = new Markup(
                $"[deepskyblue1]{Markup.Escape(GetSpinner(tick, useUnicode))}[/] "
                + $"[grey]Initializing deploy... (elapsed {elapsedText})[/]"
            );
        }

        var body = new Rows([
            header,
            CliStyle.SectionRule("Events", "deepskyblue1"),
            CreateEventsTable(events),
        ]);

        return new Panel(body) { Header = new PanelHeader($"Deploy {shortId}"), Border = BoxBorder.Rounded };
    }

    private static IRenderable CreateHeader(DeployRecord deploy, int tick, DateTimeOffset startedAt, bool useUnicode)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        var elapsedText = FormatElapsed(elapsed);
        var refLabel = deploy.IsTag ? $"tag: {deploy.Ref}" : $"branch: {deploy.Ref}";

        var pipelineInfo = BuildPipelineInfo(deploy);

        var (prefix, color, activity) = deploy.Status switch
        {
            DeployStatus.Completed => (useUnicode ? "✓" : "*", "green", "Deploy completed."),
            DeployStatus.Failed => (useUnicode ? "✗" : "!", "red", $"Failed during {FriendlyPhase(deploy.Phase)}."),
            _ => (GetSpinner(tick, useUnicode), "deepskyblue1", $"{FriendlyPhase(deploy.Phase)}…"),
        };

        var rows = new List<IRenderable>
        {
            new Markup($"[bold]{Markup.Escape(refLabel)}[/]  {pipelineInfo}"),
            new Markup($"[{color}]{Markup.Escape(prefix)}[/] {Markup.Escape(activity)} [grey](elapsed {elapsedText})[/]"),
            CreatePhaseMarkup(deploy.Phase, deploy.Status, useUnicode),
        };

        return new Rows(rows);
    }

    private static string BuildPipelineInfo(DeployRecord deploy)
    {
        var parts = new List<string>();
        if (deploy.BackendPipelineId.HasValue)
        {
            var info = $"Backend #{deploy.BackendPipelineId}";
            if (deploy.BackendRunId.HasValue) info += $" run #{deploy.BackendRunId}";
            parts.Add($"[grey]{Markup.Escape(info)}[/]");
        }
        if (deploy.FrontendPipelineId.HasValue)
        {
            var info = $"Frontend #{deploy.FrontendPipelineId}";
            if (deploy.FrontendRunId.HasValue) info += $" run #{deploy.FrontendRunId}";
            parts.Add($"[grey]{Markup.Escape(info)}[/]");
        }
        return parts.Count > 0 ? string.Join("  ·  ", parts) : "[grey]-[/]";
    }

    private static IRenderable CreatePhaseMarkup(DeployPhase currentPhase, DeployStatus status, bool useUnicode)
    {
        var arrow = useUnicode ? " [grey]→[/] " : " [grey]->[/] ";
        var currentIndex = Array.IndexOf(PhaseSequence, currentPhase);

        var entries = PhaseSequence.Select((phase, index) =>
        {
            var color = (status, index < currentIndex, index == currentIndex && status == DeployStatus.Failed) switch
            {
                (DeployStatus.Completed, _, _) => "green",
                (_, true, _) => "green",
                (_, _, true) => "red",
                _ => "deepskyblue1",
            };
            return $"[{color}]{Markup.Escape(PhaseLabel(phase))}[/]";
        }).ToArray();

        return new Markup(string.Join(arrow, entries));
    }

    private static Table CreateEventsTable(IReadOnlyList<DeployEventRecord> events)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Time", "Message");
        var recent = events.TakeLast(8).ToArray();
        if (recent.Length == 0)
        {
            table.AddRow("-", "[grey]No events yet.[/]");
            return table;
        }
        foreach (var ev in recent)
            table.AddRow(ev.Timestamp.ToLocalTime().ToString("HH:mm:ss"), Markup.Escape(ev.Message));
        return table;
    }

    private static string BuildTitle(DeployRecord? deploy, int tick, bool useUnicode)
    {
        if (deploy is null)
            return TerminalTitleFormatter.BuildWaitingTitle("Deploy", tick, useUnicode);

        var (phaseText, prefix) = deploy.Status switch
        {
            DeployStatus.Completed => ("Completed", useUnicode ? "✓" : "*"),
            DeployStatus.Failed => ($"Failed ({FriendlyPhase(deploy.Phase)})", useUnicode ? "✗" : "!"),
            _ => (FriendlyPhase(deploy.Phase), GetSpinner(tick, useUnicode)),
        };

        return $"{prefix} Deploy | {phaseText} | ado-worker";
    }

    private static string FriendlyPhase(DeployPhase phase) => phase switch
    {
        DeployPhase.TriggeringPipelines => "Triggering",
        DeployPhase.WaitingForPipelines => "Waiting",
        DeployPhase.ComputingChanges => "Computing",
        DeployPhase.ApplyingChanges => "Applying",
        DeployPhase.Done => "Done",
        _ => phase.ToString(),
    };

    private static string PhaseLabel(DeployPhase phase) => phase switch
    {
        DeployPhase.TriggeringPipelines => "Triggering",
        DeployPhase.WaitingForPipelines => "Waiting",
        DeployPhase.ComputingChanges => "Computing",
        DeployPhase.ApplyingChanges => "Applying",
        _ => phase.ToString(),
    };

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");

    private static string GetSpinner(int tick, bool useUnicode)
    {
        var frames = useUnicode ? UnicodeSpinnerFrames : AsciiSpinnerFrames;
        return frames[Math.Abs(tick) % frames.Length];
    }
}
