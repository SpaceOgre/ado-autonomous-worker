using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Ado.AutonomousWorker.Cli.Services;

public sealed class ExecutionLiveDisplay(IStateStore stateStore)
{
    private static readonly TaskPhase[] OrderedPhases =
    [
        TaskPhase.Queued,
        TaskPhase.Planning,
        TaskPhase.Coding,
        TaskPhase.Reviewing,
        TaskPhase.ApplyingReview,
        TaskPhase.Validating,
        TaskPhase.Publishing,
        TaskPhase.Completed,
    ];

    public Task<string> RunForWorkItemAsync(
        int workItemId,
        Func<CancellationToken, Task<string>> operation,
        CancellationToken cancellationToken)
    {
        return RunAsync(
            $"Work item #{workItemId}",
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

        var operationTask = operation(cancellationToken);
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return await operationTask;
        }

        await AnsiConsole.Live(CreateWaitingView(title))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async context =>
            {
                while (!operationTask.IsCompleted)
                {
                    var execution = await executionResolver(cancellationToken);
                    context.UpdateTarget(await BuildViewAsync(title, execution, cancellationToken));
                    await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
                }

                var finalExecution = await executionResolver(cancellationToken);
                context.UpdateTarget(await BuildViewAsync(title, finalExecution, cancellationToken));
            });

        return await operationTask;
    }

    private async Task<IRenderable> BuildViewAsync(string title, TaskExecutionRecord? execution, CancellationToken cancellationToken)
    {
        if (execution is null)
        {
            return CreateWaitingView(title);
        }

        var events = await stateStore.GetEventsAsync(execution.ExecutionId, cancellationToken);
        var sessions = await stateStore.GetAgentSessionsAsync(execution.ExecutionId, cancellationToken);
        var agentOutput = await stateStore.GetAgentOutputChunksAsync(execution.ExecutionId, cancellationToken);
        var plan = await stateStore.GetPlanAsync(execution.ExecutionId, cancellationToken);

        var body = new Rows(
        [
            CreateSummaryTable(execution),
            new Rule("Progress"),
            CreatePhaseMarkup(execution.Phase),
            new Rule("Recent Events"),
            CreateEventsTable(events),
            new Rule("Plan"),
            CreatePlanTable(plan),
            new Rule("Agent Sessions"),
            CreateSessionsTable(sessions),
            new Rule("Agent Output"),
            CreateAgentOutputTable(agentOutput),
        ]);

        return new Panel(body)
        {
            Header = new PanelHeader(title),
            Border = BoxBorder.Rounded,
        };
    }

    private static IRenderable CreateWaitingView(string title)
    {
        var body = new Rows(
        [
            new Markup("[grey]Waiting for execution state...[/]"),
            new Markup("[grey]The execution record will appear here as soon as it is persisted.[/]"),
        ]);

        return new Panel(body)
        {
            Header = new PanelHeader(title),
            Border = BoxBorder.Rounded,
        };
    }

    private static Table CreateSummaryTable(TaskExecutionRecord execution)
    {
        var table = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumns("Field", "Value");
        table.AddRow("Execution", execution.ExecutionId);
        table.AddRow("Work Item", execution.WorkItemId.ToString());
        table.AddRow("Kind", execution.Kind.ToString());
        table.AddRow("Phase", FormatPhase(execution.Phase));
        table.AddRow("Updated", execution.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        table.AddRow("Title", Markup.Escape(execution.Title));
        return table;
    }

    private static IRenderable CreatePhaseMarkup(TaskPhase currentPhase)
    {
        var entries = OrderedPhases
            .Select(phase => FormatPipelinePhase(phase, currentPhase))
            .ToArray();

        return new Markup(string.Join(" [grey]->[/] ", entries));
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
                FormatPhase(executionEvent.Phase),
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

    private static Table CreateSessionsTable(IReadOnlyList<AgentSessionHandle> sessions)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Role", "Provider", "Workdir", "Transcript");
        var recentSessions = sessions.TakeLast(4).ToArray();
        if (recentSessions.Length == 0)
        {
            table.AddRow("-", "-", "[grey]-[/]", "[grey]-[/]");
            return table;
        }

        foreach (var session in recentSessions)
        {
            table.AddRow(
                session.Role.ToString(),
                session.Provider.ToString(),
                Markup.Escape(ShortenPath(session.WorkingDirectory)),
                Markup.Escape(ShortenPath(session.TranscriptPath)));
        }

        return table;
    }

    private static Table CreateAgentOutputTable(IReadOnlyList<AgentOutputChunk> chunks)
    {
        var table = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumns("Field", "Value");
        if (chunks.Count == 0)
        {
            table.AddRow("Output", "[grey]Waiting for agent output...[/]");
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

        table.AddRow("Role", latestChunk.Role.ToString());
        table.AddRow("Provider", latestChunk.Provider.ToString());
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

    private static string FormatPipelinePhase(TaskPhase phase, TaskPhase currentPhase)
    {
        var color = phase == currentPhase
            ? "deepskyblue1"
            : IsPastPhase(phase, currentPhase)
                ? "green"
                : "grey";

        return $"[{color}]{Markup.Escape(phase.ToString())}[/]";
    }

    private static bool IsPastPhase(TaskPhase phase, TaskPhase currentPhase)
    {
        return Array.IndexOf(OrderedPhases, phase) < Array.IndexOf(OrderedPhases, currentPhase);
    }

    private static string FormatPhase(TaskPhase phase)
    {
        var color = phase switch
        {
            TaskPhase.Completed => "green",
            TaskPhase.Failed => "red",
            TaskPhase.Skipped => "yellow",
            _ => "deepskyblue1",
        };

        return $"[{color}]{Markup.Escape(phase.ToString())}[/]";
    }
}
