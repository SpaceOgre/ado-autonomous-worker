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
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Top)
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
        var sessions = await stateStore.GetAgentSessionsAsync(execution.ExecutionId, cancellationToken);
        var agentOutput = await stateStore.GetAgentOutputChunksAsync(execution.ExecutionId, cancellationToken);
        var plan = await stateStore.GetPlanAsync(execution.ExecutionId, cancellationToken);

        var body = new Rows(
        [
            CreateSummaryTable(execution, agentOptions.Value),
            new Rule("Activity"),
            CreateActivityMarkup(execution.Phase, execution.Status, tick, startedAt, useUnicode, agentOptions.Value),
            new Rule("Progress"),
            CreatePhaseMarkup(execution.Phase, execution.Status),
            new Rule("Recent Events"),
            CreateEventsTable(events),
            new Rule("Plan"),
            CreatePlanTable(plan),
            new Rule("Agent Sessions"),
            CreateSessionsTable(sessions, agentOutput, execution.Phase, agentOptions.Value),
            new Rule("Agent Output"),
            CreateAgentOutputTable(agentOutput, execution.Phase, tick, useUnicode),
        ]);

        return new Panel(body)
        {
            Header = new PanelHeader(title),
            Border = BoxBorder.Rounded,
        };
    }

    private static IRenderable CreateWaitingView(string title, int tick, DateTimeOffset startedAt, bool useUnicode)
    {
        var body = new Rows(
        [
            CreateWaitingMarkup(tick, startedAt, useUnicode),
            new Markup("[grey]The execution record will appear here as soon as it is persisted.[/]"),
        ]);

        return new Panel(body)
        {
            Header = new PanelHeader(title),
            Border = BoxBorder.Rounded,
        };
    }

    private static Table CreateSummaryTable(TaskExecutionRecord execution, AgentOptions agentOptions)
    {
        var table = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumns("Field", "Value");
        table.AddRow("Execution", execution.ExecutionId);
        table.AddRow("Work Item", execution.WorkItemId.ToString());
        table.AddRow("Kind", CliStyle.QueueKind(execution.Kind));
        table.AddRow("Phase", CliStyle.Phase(execution.Phase));
        table.AddRow("Status", CliStyle.Status(execution.Status));
        table.AddRow("Updated", execution.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        table.AddRow("Title", Markup.Escape(execution.Title));
        table.AddRow("Plan Agent", Markup.Escape(FormatAgent(agentOptions.Plan)));
        table.AddRow("Code Agent", Markup.Escape(FormatAgent(agentOptions.Code)));
        table.AddRow("Review Agent", Markup.Escape(FormatAgent(agentOptions.Review)));
        return table;
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

    private static IRenderable CreateWaitingMarkup(int tick, DateTimeOffset startedAt, bool useUnicode)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        var elapsedText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

        return new Markup($"[deepskyblue1]{Markup.Escape(GetSpinnerFrame(tick, useUnicode))}[/] Waiting for execution state... [grey](elapsed {elapsedText})[/]");
    }

    private static Table CreateEventsTable(IReadOnlyList<ExecutionEventRecord> events)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Time", "Phase", "Message");
        var recentEvents = events.TakeLast(6).ToArray();
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

    private static Table CreatePlanTable(PlanResult? plan)
    {
        var table = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumns("Field", "Value");
        if (plan is null)
        {
            table.AddRow("Plan", "[grey]-[/]");
            return table;
        }

        table.AddRow("Scope", plan.Scope.ToString());
        table.AddRow("Summary", Markup.Escape(plan.Summary));
        table.AddRow("Repositories", FormatList(plan.Repositories));
        table.AddRow("Validation", FormatList(plan.ValidationTargets));
        table.AddRow("Tests", FormatList(plan.TestIntent));
        table.AddRow("Risks", FormatList(plan.Risks));
        return table;
    }

    private static Table CreateSessionsTable(
        IReadOnlyList<AgentSessionHandle> sessions,
        IReadOnlyList<AgentOutputChunk> chunks,
        TaskPhase currentPhase,
        AgentOptions agentOptions)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Role", "Provider", "Workdir", "Transcript");
        var recentSessions = sessions.TakeLast(4).ToArray();
        if (recentSessions.Length == 0 && chunks.Count > 0)
        {
            recentSessions = chunks
                .GroupBy(chunk => chunk.SessionId)
                .OrderByDescending(group => group.Max(item => item.Timestamp))
                .Take(4)
                .Select(group =>
                {
                    var latest = group.Last();
                    return new AgentSessionHandle(
                        latest.SessionId,
                        latest.Provider,
                        latest.Role,
                        latest.WorkingDirectory,
                        "Live output only");
                })
                .ToArray();
        }

        if (recentSessions.Length == 0)
        {
            var activeRole = GetRoleForPhase(currentPhase);
            var activeConfig = GetConfigForPhase(currentPhase, agentOptions);
            if (activeRole is not null && activeConfig is not null)
            {
                table.AddRow(
                    CliStyle.Role(activeRole.Value),
                    CliStyle.Provider(activeConfig.Provider),
                    "[grey]Waiting for session...[/]",
                    "[grey]-[/]");
            }
            else
            {
                table.AddRow("-", "-", "[grey]-[/]", "[grey]-[/]");
            }

            return table;
        }

        foreach (var session in recentSessions)
        {
            table.AddRow(
                CliStyle.Role(session.Role),
                CliStyle.Provider(session.Provider),
                Markup.Escape(ShortenPath(session.WorkingDirectory)),
                Markup.Escape(ShortenPath(session.TranscriptPath)));
        }

        return table;
    }

    private static Table CreateAgentOutputTable(IReadOnlyList<AgentOutputChunk> chunks, TaskPhase phase, int tick, bool useUnicode)
    {
        var table = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumns("Field", "Value");
        if (chunks.Count == 0)
        {
            var spinner = GetSpinnerFrame(tick, useUnicode);
            var waitingText = phase is TaskPhase.Publishing
                ? "[grey]No agent output captured.[/]"
                : $"[grey]{Markup.Escape(spinner)} Waiting for agent output...[/]";

            table.AddRow("Output", waitingText);
            return table;
        }

        var activeSession = chunks
            .GroupBy(chunk => chunk.SessionId)
            .OrderByDescending(group => group.Max(item => item.Timestamp))
            .First();

        var latestChunk = activeSession.Last();
        var excerpt = string.Join(
            Environment.NewLine,
            activeSession
                .TakeLast(20)
                .Select(chunk => chunk.Content));

        table.AddRow("Role", CliStyle.Role(latestChunk.Role));
        table.AddRow("Provider", CliStyle.Provider(latestChunk.Provider));
        table.AddRow("Workdir", Markup.Escape(ShortenPath(latestChunk.WorkingDirectory)));
        table.AddRow("Updated", latestChunk.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        table.AddRow("Excerpt", Markup.Escape(excerpt));
        return table;
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0
            ? "[grey]-[/]"
            : Markup.Escape(string.Join(Environment.NewLine, values));
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

    private static AgentRole? GetRoleForPhase(TaskPhase phase)
    {
        return phase switch
        {
            TaskPhase.Planning => AgentRole.Plan,
            TaskPhase.Coding => AgentRole.Code,
            TaskPhase.Reviewing => AgentRole.Review,
            TaskPhase.ApplyingReview => AgentRole.Code,
            _ => null,
        };
    }

    private static string GetSpinnerFrame(int tick, bool useUnicode)
    {
        var frames = useUnicode ? UnicodeSpinnerFrames : AsciiSpinnerFrames;
        return frames[Math.Abs(tick) % frames.Length];
    }

}
