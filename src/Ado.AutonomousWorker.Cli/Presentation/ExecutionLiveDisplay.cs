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
    IOptions<AgentOptions> agentOptions)
{
    private static readonly string[] UnicodeSpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] AsciiSpinnerFrames = ["-", "\\", "|", "/"];
    private static readonly TaskPhase[] OrderedPhases =
    [
        TaskPhase.Queued,
        TaskPhase.Planning,
        TaskPhase.Coding,
        TaskPhase.Reviewing,
        TaskPhase.ApplyingReview,
        TaskPhase.Validating,
        TaskPhase.Publishing,
    ];

    public Task<string> RunForWorkItemAsync(
        int workItemId,
        Func<CancellationToken, Task<string>> operation,
        CancellationToken cancellationToken)
    {
        return RunForWorkItemAsync(
            $"Work item #{workItemId}",
            workItemId,
            operation,
            cancellationToken);
    }

    public Task<string> RunForWorkItemAsync(
        string title,
        int workItemId,
        Func<CancellationToken, Task<string>> operation,
        CancellationToken cancellationToken)
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
            cancellationToken);
    }

    public Task<string> RunForExecutionAsync(
        string executionId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        return RunAsync(
            $"Execution {executionId}",
            async token =>
            {
                await operation(token);
                return executionId;
            },
            token => stateStore.GetExecutionAsync(executionId, token),
            cancellationToken);
    }

    private async Task<string> RunAsync(
        string title,
        Func<CancellationToken, Task<string>> operation,
        Func<CancellationToken, Task<TaskExecutionRecord?>> executionResolver,
        CancellationToken cancellationToken)
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

        await AnsiConsole.Live(CreateWaitingView(title, tick, startedAt, useUnicode))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Visible)
            .StartAsync(async context =>
            {
                while (!operationTask.IsCompleted)
                {
                    var execution = await executionResolver(cancellationToken);
                    terminalTitleService.UpdateTitle(TerminalTitleFormatter.BuildExecutionTitle(title, execution, tick++, useUnicode));
                    context.UpdateTarget(await BuildViewAsync(title, execution, tick, startedAt, useUnicode, cancellationToken));
                    await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
                }

                var finalExecution = await executionResolver(cancellationToken);
                terminalTitleService.UpdateTitle(TerminalTitleFormatter.BuildExecutionTitle(title, finalExecution, tick, useUnicode));
                context.UpdateTarget(await BuildViewAsync(title, finalExecution, tick, startedAt, useUnicode, cancellationToken));
            });

        return await operationTask;
    }

    private async Task<IRenderable> BuildViewAsync(
        string title,
        TaskExecutionRecord? execution,
        int tick,
        DateTimeOffset startedAt,
        bool useUnicode,
        CancellationToken cancellationToken)
    {
        if (execution is null)
        {
            return CreateWaitingView(title, tick, startedAt, useUnicode);
        }

        var events = await stateStore.GetEventsAsync(execution.ExecutionId, cancellationToken);
        var agentOutput = await stateStore.GetAgentOutputChunksAsync(execution.ExecutionId, cancellationToken);

        var body = new Rows(
        [
            CreateCompactHeader(execution, tick, startedAt, useUnicode, agentOptions.Value),
            CliStyle.SectionRule("Recent Events", "deepskyblue1"),
            CreateEventsTable(events),
            CliStyle.SectionRule("Agent Output", "mediumspringgreen"),
            CreateAgentOutputMarkup(agentOutput, execution.Phase, tick, useUnicode),
        ]);

        return new Panel(body)
        {
            Header = new PanelHeader(title),
            Border = BoxBorder.Rounded,
        };
    }

    private static IRenderable CreateWaitingView(string title, int tick, DateTimeOffset startedAt, bool useUnicode)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        var elapsedText = elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        var spinner = GetSpinnerFrame(tick, useUnicode);

        return new Panel(new Markup($"[deepskyblue1]{Markup.Escape(spinner)}[/] Waiting for execution state... [grey](elapsed {elapsedText})[/]"))
        {
            Header = new PanelHeader(title),
            Border = BoxBorder.Rounded,
        };
    }

    // Compact 4-line header replacing the old 13-line summary table + separate activity + progress sections.
    private static IRenderable CreateCompactHeader(
        TaskExecutionRecord execution,
        int tick,
        DateTimeOffset startedAt,
        bool useUnicode,
        AgentOptions agentOptions)
    {
        var shortId = execution.ExecutionId.Length > 8
            ? execution.ExecutionId[..8] + "…"
            : execution.ExecutionId;

        return new Rows(
        [
            // Title + work item identity
            new Markup(
                $"[bold]{Markup.Escape(execution.Title)}[/]  " +
                $"[grey]#{execution.WorkItemId} · {CliStyle.QueueKind(execution.Kind)}[/]"),

            // Execution ID + agent configuration
            new Markup(
                $"[grey]Exec[/] {Markup.Escape(shortId)}  " +
                $"[grey]Plan[/] {Markup.Escape(FormatAgent(agentOptions.Plan))}  " +
                $"[grey]Code[/] {Markup.Escape(FormatAgent(agentOptions.Code))}  " +
                $"[grey]Review[/] {Markup.Escape(FormatAgent(agentOptions.Review))}"),

            // Activity status line with spinner / outcome icon
            CreateActivityMarkup(execution.Phase, execution.Status, tick, startedAt, useUnicode, agentOptions),

            // Phase pipeline
            CreatePhaseMarkup(execution.Phase, execution.Status),
        ]);
    }

    private static IRenderable CreatePhaseMarkup(TaskPhase currentPhase, ExecutionStatus status)
    {
        var entries = OrderedPhases
            .Select(phase => FormatPipelinePhase(phase, currentPhase, status))
            .ToArray();

        return new Markup(string.Join(" [grey]->[/] ", entries));
    }

    private static IRenderable CreateActivityMarkup(
        TaskPhase phase,
        ExecutionStatus status,
        int tick,
        DateTimeOffset startedAt,
        bool useUnicode,
        AgentOptions agentOptions)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        var elapsedText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

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

        return new Markup($"[{color}]{Markup.Escape(prefix)}[/] {Markup.Escape(message)}{agentSuffix} [grey](elapsed {elapsedText})[/]");
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
                Markup.Escape(executionEvent.Message));
        }

        return table;
    }

    // Plain markup lines — no table overhead. One header line (role/provider/workdir/time)
    // followed by the most recent output chunks.
    private static IRenderable CreateAgentOutputMarkup(
        IReadOnlyList<AgentOutputChunk> chunks,
        TaskPhase phase,
        int tick,
        bool useUnicode)
    {
        if (chunks.Count == 0)
        {
            var spinner = GetSpinnerFrame(tick, useUnicode);
            var waitingText = phase is TaskPhase.Publishing
                ? "No agent output captured."
                : $"{spinner} Waiting for agent output...";
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
                $"{CliStyle.Role(latestChunk.Role)} {CliStyle.Provider(latestChunk.Provider)}  " +
                $"[grey]{Markup.Escape(ShortenPath(latestChunk.WorkingDirectory))} · {latestChunk.Timestamp.ToLocalTime():HH:mm:ss}[/]"),
        };

        foreach (var chunk in activeSession.TakeLast(5))
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

    private static string FormatPipelinePhase(TaskPhase phase, TaskPhase currentPhase, ExecutionStatus status)
    {
        string color;
        if (status == ExecutionStatus.Completed)
            color = "green";
        else if (status == ExecutionStatus.Failed && phase == currentPhase)
            color = "red";
        else if (phase == currentPhase)
            color = "deepskyblue1";
        else if (IsPastPhase(phase, currentPhase))
            color = "green";
        else
            color = "grey";

        return $"[{color}]{Markup.Escape(phase.ToString())}[/]";
    }

    private static bool IsPastPhase(TaskPhase phase, TaskPhase currentPhase)
    {
        return Array.IndexOf(OrderedPhases, phase) < Array.IndexOf(OrderedPhases, currentPhase);
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
            _ => null,
        };
    }

    private static string GetSpinnerFrame(int tick, bool useUnicode)
    {
        var frames = useUnicode ? UnicodeSpinnerFrames : AsciiSpinnerFrames;
        return frames[Math.Abs(tick) % frames.Length];
    }
}
