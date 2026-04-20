using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Ado.AutonomousWorker.Cli.Presentation;

public sealed class ExecutionLiveDisplay(
    IStateStore stateStore,
    TerminalTitleService terminalTitleService,
    IOptions<AgentOptions> agentOptions
)
{
    private static readonly string[] UnicodeSpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] AsciiSpinnerFrames = ["-", "\\", "|", "/"];

    public Task<string> RunForWorkItemAsync(
        int workItemId,
        Func<CancellationToken, Task<string>> operation,
        CancellationToken cancellationToken
    )
    {
        return RunForWorkItemAsync($"Work item #{workItemId}", workItemId, operation, cancellationToken);
    }

    public Task<string> RunForWorkItemAsync(
        string title,
        int workItemId,
        Func<CancellationToken, Task<string>> operation,
        CancellationToken cancellationToken
    )
    {
        return RunAsync(
            title,
            operation,
            async token =>
            {
                var executions = await stateStore.ListExecutionsAsync(token);
                return executions
                    .Where(item => item.WorkItemId == workItemId)
                    .OrderByDescending(item => item.CreatedAt)
                    .FirstOrDefault();
            },
            cancellationToken
        );
    }

    public Task<string> RunForExecutionAsync(
        string executionId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken
    )
    {
        return RunAsync(
            $"Execution {executionId}",
            async token =>
            {
                await operation(token);
                return executionId;
            },
            token => stateStore.GetExecutionAsync(executionId, token),
            cancellationToken
        );
    }

    private sealed record ViewData(
        TaskExecutionRecord Execution,
        IReadOnlyList<ExecutionEventRecord> Events,
        IReadOnlyList<AgentOutputChunk> AgentOutput,
        IReadOnlyList<AgentUsageRecord> UsageRecords
    );

    private async Task<string> RunAsync(
        string title,
        Func<CancellationToken, Task<string>> operation,
        Func<CancellationToken, Task<TaskExecutionRecord?>> executionResolver,
        CancellationToken cancellationToken
    )
    {
        await stateStore.InitializeAsync(cancellationToken);

        using var titleScope = terminalTitleService.BeginScope();
        var useUnicode = AnsiConsole.Profile.Capabilities.Unicode;
        var startedAt = DateTimeOffset.UtcNow;
        var tick = 0;

        terminalTitleService.UpdateTitle(TerminalTitleFormatter.BuildWaitingTitle(title, tick++, useUnicode));

        var operationTask = operation(cancellationToken);
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return await operationTask;
        }

        const int SpinnerMs = 100;
        const int DataRefreshMs = 750;
        const int RefreshEvery = DataRefreshMs / SpinnerMs;

        // Pre-fetch before Live() starts so the initial frame height matches subsequent frames.
        // A null→data transition mid-display confuses Spectre's cursor-up line count.
        var loopIteration = 0;
        var cached = await FetchViewDataAsync(executionResolver, cancellationToken);
        ViewData? finalData = null;

        if (cached is not null)
            loopIteration = 1; // skip immediate re-fetch since we just loaded data

        await AnsiConsole
            .Live(BuildView(title, cached, tick, startedAt, useUnicode))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Visible)
            .StartAsync(async liveCtx =>
            {
                while (!operationTask.IsCompleted)
                {
                    if (loopIteration % RefreshEvery == 0)
                        cached = await FetchViewDataAsync(executionResolver, cancellationToken) ?? cached;

                    terminalTitleService.UpdateTitle(
                        TerminalTitleFormatter.BuildExecutionTitle(title, cached?.Execution, tick, useUnicode)
                    );

                    liveCtx.UpdateTarget(BuildView(title, cached, tick, startedAt, useUnicode));
                    tick++;
                    loopIteration++;
                    await Task.Delay(SpinnerMs, cancellationToken);
                }

                finalData = await FetchViewDataAsync(executionResolver, cancellationToken);

                terminalTitleService.UpdateTitle(
                    TerminalTitleFormatter.BuildExecutionTitle(title, finalData?.Execution, tick, useUnicode)
                );
                liveCtx.UpdateTarget(BuildView(title, finalData, tick, startedAt, useUnicode));
            });

        return await operationTask;
    }

    private async Task<ViewData?> FetchViewDataAsync(
        Func<CancellationToken, Task<TaskExecutionRecord?>> executionResolver,
        CancellationToken cancellationToken
    )
    {
        var execution = await executionResolver(cancellationToken);
        if (execution is null)
            return null;

        var events = await stateStore.GetEventsAsync(execution.ExecutionId, cancellationToken);
        var agentOutput = await stateStore.GetAgentOutputChunksAsync(execution.ExecutionId, cancellationToken);
        var usageRecords = await stateStore.GetAgentUsageAsync(execution.ExecutionId, cancellationToken);
        return new ViewData(execution, events, agentOutput, usageRecords);
    }

    private IRenderable BuildView(string title, ViewData? data, int tick, DateTimeOffset startedAt, bool useUnicode)
    {
        IRenderable header;
        IReadOnlyList<ExecutionEventRecord> events = [];
        IReadOnlyList<AgentOutputChunk> agentOutput = [];
        var phase = TaskPhase.Queued;

        if (data is not null)
        {
            header = CreateCompactHeader(
                data.Execution,
                tick,
                startedAt,
                useUnicode,
                agentOptions.Value,
                data.UsageRecords,
                data.Events
            );
            events = data.Events;
            agentOutput = data.AgentOutput;
            phase = data.Execution.Phase;
        }
        else
        {
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            var elapsedText = elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");
            header = new Markup(
                $"[deepskyblue1]{Markup.Escape(GetSpinnerFrame(tick, useUnicode))}[/] "
                    + $"[grey]Waiting for execution data... (elapsed {elapsedText})[/]"
            );
        }

        // Always render the full panel structure so the height stays consistent across
        // all states. A height jump (small→large) causes Spectre's cursor tracking to
        // land short, leaving the previous frame's top line visible as a duplicate.
        var body = new Rows([
            header,
            CliStyle.SectionRule("Recent Events", "deepskyblue1"),
            CreateEventsTable(events),
            CliStyle.SectionRule("Agent Output", "mediumspringgreen"),
            CreateAgentOutputMarkup(agentOutput, phase, tick, useUnicode),
        ]);

        return new Panel(body) { Header = new PanelHeader(title), Border = BoxBorder.Rounded };
    }

    // Compact header: title, agent config, activity, phase pipeline, and (when available) running token/cost totals.
    private static IRenderable CreateCompactHeader(
        TaskExecutionRecord execution,
        int tick,
        DateTimeOffset startedAt,
        bool useUnicode,
        AgentOptions agentOptions,
        IReadOnlyList<AgentUsageRecord> usageRecords,
        IReadOnlyList<ExecutionEventRecord> events
    )
    {
        var shortId = execution.ExecutionId.Length > 8 ? execution.ExecutionId[..8] + "…" : execution.ExecutionId;

        var prSuffix = execution.PullRequests is { Count: > 0 }
            ? "  " + string.Join(" ", execution.PullRequests.Select(pr => $"[green]!{pr.PullRequestId}[/]"))
            : string.Empty;

        var rows = new List<IRenderable>
        {
            // Title + work item identity + PR(s) once published
            new Markup(
                $"[bold]{Markup.Escape(execution.Title)}[/]  "
                    + $"[grey]#{execution.WorkItemId} · {CliStyle.QueueKind(execution.Kind)}[/]"
                    + prSuffix
            ),
            // Execution ID + agent configuration
            new Markup(
                $"[grey]Exec[/] {Markup.Escape(shortId)}  "
                    + $"[grey]Plan[/] {Markup.Escape(FormatAgent(agentOptions.Plan))}  "
                    + $"[grey]Code[/] {Markup.Escape(FormatAgent(agentOptions.Code))}  "
                    + $"[grey]Review[/] {Markup.Escape(FormatAgent(agentOptions.Review))}  "
                    + $"[grey]PR[/] {Markup.Escape(FormatAgent(agentOptions.Pr))}"
            ),
            // Activity status line with spinner / outcome icon
            CreateActivityMarkup(execution.Phase, execution.Status, tick, startedAt, useUnicode, agentOptions),
            // Phase pipeline
            CreatePhaseMarkup(execution.Phase, execution.Status, events),
        };

        // Token / cost totals — always rendered to keep header height stable across updates.
        string usageLine;
        if (usageRecords.Count > 0)
        {
            var totalIn = usageRecords.Sum(u => u.InputTokens);
            var totalOut = usageRecords.Sum(u => u.OutputTokens);
            var totalCached = usageRecords.Sum(u => u.CachedInputTokens);
            var totalCost = usageRecords.Sum(u => u.CostUsd ?? 0m);

            usageLine = $"[grey]Tokens: {totalIn:N0} in / {totalOut:N0} out";
            if (totalCached > 0)
                usageLine += $" ({totalCached:N0} cached)";
            if (totalCost > 0m)
                usageLine += $"  ·  ${totalCost:F4}";
            usageLine += "[/]";
        }
        else
        {
            usageLine = "[grey]Tokens: -[/]";
        }
        rows.Add(new Markup(usageLine));

        return new Rows(rows);
    }

    private static IRenderable CreatePhaseMarkup(
        TaskPhase currentPhase,
        ExecutionStatus status,
        IReadOnlyList<ExecutionEventRecord> events
    )
    {
        // Build phase sequence from events, deduplicating consecutive same-phase entries
        // so retry loops render correctly: Validating -> FixingValidation -> Validating
        var sequence = new List<TaskPhase>();
        foreach (var e in events)
        {
            if (sequence.Count == 0 || sequence[^1] != e.Phase)
                sequence.Add(e.Phase);
        }
        if (sequence.Count == 0 || sequence[^1] != currentPhase)
            sequence.Add(currentPhase);

        var currentIndex = status == ExecutionStatus.Completed ? sequence.Count : sequence.LastIndexOf(currentPhase);

        var entries = sequence
            .Select(
                (phase, index) =>
                {
                    var color = (
                        status,
                        index < currentIndex,
                        index == currentIndex,
                        status == ExecutionStatus.Failed
                    ) switch
                    {
                        (ExecutionStatus.Completed, _, _, _) => "green",
                        (_, true, _, _) => "green",
                        (_, _, true, true) => "red",
                        _ => "deepskyblue1",
                    };
                    return $"[{color}]{Markup.Escape(phase.ToString())}[/]";
                }
            )
            .ToArray();

        return new Markup(string.Join(" [grey]->[/] ", entries));
    }

    private static IRenderable CreateActivityMarkup(
        TaskPhase phase,
        ExecutionStatus status,
        int tick,
        DateTimeOffset startedAt,
        bool useUnicode,
        AgentOptions agentOptions
    )
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        var elapsedText = elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");

        var (prefix, color, message) = status switch
        {
            ExecutionStatus.Completed => (useUnicode ? "✓" : "*", "green", "Execution completed."),
            ExecutionStatus.Failed => (useUnicode ? "✗" : "!", "red", $"Failed during {phase}."),
            ExecutionStatus.Skipped => (useUnicode ? "↷" : "~", "yellow", "Execution skipped."),
            _ => (GetSpinnerFrame(tick, useUnicode), "deepskyblue1", $"{phase} in progress..."),
        };

        var agentText = GetAgentForPhase(phase, agentOptions);
        var agentSuffix = string.IsNullOrWhiteSpace(agentText)
            ? string.Empty
            : $" [grey](via {Markup.Escape(agentText)})[/]";

        return new Markup(
            $"[{color}]{Markup.Escape(prefix)}[/] {Markup.Escape(message)}{agentSuffix} [grey](elapsed {elapsedText})[/]"
        );
    }

    private static Table CreateEventsTable(IReadOnlyList<ExecutionEventRecord> events)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Time", "Phase", "Message");
        var recentEvents = events.TakeLast(8).ToArray();
        if (recentEvents.Length == 0)
        {
            table.AddRow("-", "-", "[grey]No events recorded yet.[/]");
            return table;
        }

        foreach (var executionEvent in recentEvents)
        {
            table.AddRow(
                executionEvent.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
                CliStyle.Phase(executionEvent.Phase),
                Markup.Escape(executionEvent.Message)
            );
        }

        return table;
    }

    // Plain markup lines — no table overhead. One header line (role/provider/workdir/time)
    // followed by the most recent output chunks.
    private static IRenderable CreateAgentOutputMarkup(
        IReadOnlyList<AgentOutputChunk> chunks,
        TaskPhase phase,
        int tick,
        bool useUnicode
    )
    {
        if (chunks.Count == 0)
        {
            var spinner = GetSpinnerFrame(tick, useUnicode);
            var waitingText =
                phase is TaskPhase.Publishing ? "No agent output captured." : $"{spinner} Waiting for agent output...";
            return new Markup($"[grey]{Markup.Escape(waitingText)}[/]");
        }

        var activeSession = chunks
            .GroupBy(chunk => chunk.SessionId)
            .OrderByDescending(group => group.Max(item => item.Timestamp))
            .First();

        var latestChunk = activeSession.Last();
        var lines = new List<IRenderable>
        {
            new Markup(
                $"{CliStyle.Role(latestChunk.Role)} {CliStyle.Provider(latestChunk.Provider)}  "
                    + $"[grey]{Markup.Escape(ShortenPath(latestChunk.WorkingDirectory))} · {latestChunk.Timestamp.ToLocalTime():HH:mm:ss}[/]"
            ),
        };

        foreach (var chunk in activeSession.TakeLast(10))
        {
            lines.Add(new Markup($"[grey]{Markup.Escape(chunk.Content)}[/]"));
        }

        return new Rows(lines);
    }

    private static string ShortenPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var tail = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return path;
        }

        var parent = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(parent) ? tail : Path.Combine(parent, tail);
    }

    private static string FormatAgent(AgentRoleConfig config)
    {
        return $"{config.Provider} / {config.Model}";
    }

    private static string? GetAgentForPhase(TaskPhase phase, AgentOptions agentOptions)
    {
        var config = GetConfigForPhase(phase, agentOptions);
        return config is null ? null : FormatAgent(config);
    }

    private static AgentRoleConfig? GetConfigForPhase(TaskPhase phase, AgentOptions agentOptions)
    {
        return phase switch
        {
            TaskPhase.Planning => agentOptions.Plan,
            TaskPhase.Coding => agentOptions.Code,
            TaskPhase.Reviewing => agentOptions.Review,
            TaskPhase.ApplyingReview => agentOptions.Code,
            TaskPhase.FixingValidation => agentOptions.Code,
            TaskPhase.Publishing => agentOptions.Pr,
            _ => null,
        };
    }

    private static string GetSpinnerFrame(int tick, bool useUnicode)
    {
        var frames = useUnicode ? UnicodeSpinnerFrames : AsciiSpinnerFrames;
        return frames[Math.Abs(tick) % frames.Length];
    }
}
