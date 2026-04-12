using System.ComponentModel;
using Ado.AutonomousWorker.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class ShowExecutionCommand(IStateStore stateStore) : AsyncCommand<ShowExecutionCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Show a specific local execution by its execution id.")]
        [CommandOption("--id <EXECUTION_ID>")]
        public string? ExecutionId { get; init; }

        [Description("Show the most recently updated local execution.")]
        [CommandOption("--last")]
        public bool Last { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await stateStore.InitializeAsync(cancellationToken);

        var hasExecutionId = !string.IsNullOrWhiteSpace(settings.ExecutionId);
        if (settings.Last == hasExecutionId)
        {
            AnsiConsole.MarkupLine("[red]Specify exactly one of:[/] [yellow]--id <execution-id>[/] or [yellow]--last[/]");
            return 1;
        }

        var execution = settings.Last
            ? (await stateStore.ListExecutionsAsync(cancellationToken)).FirstOrDefault()
            : await stateStore.GetExecutionAsync(settings.ExecutionId!, cancellationToken);

        if (execution is null)
        {
            var requestedExecution = settings.Last ? "<latest>" : settings.ExecutionId!;
            AnsiConsole.MarkupLine($"[red]Execution not found:[/] {Markup.Escape(requestedExecution)}");
            return 1;
        }

        var events = await stateStore.GetEventsAsync(execution.ExecutionId, cancellationToken);
        var sessions = await stateStore.GetAgentSessionsAsync(execution.ExecutionId, cancellationToken);
        var agentOutput = await stateStore.GetAgentOutputChunksAsync(execution.ExecutionId, cancellationToken);
        var plan = await stateStore.GetPlanAsync(execution.ExecutionId, cancellationToken);

        var summary = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumns("Field", "Value");
        summary.AddRow("Execution Id", execution.ExecutionId);
        summary.AddRow("Work Item", execution.WorkItemId.ToString());
        summary.AddRow("Title", Markup.Escape(execution.Title));
        summary.AddRow("Kind", execution.Kind.ToString());
        summary.AddRow("Phase", execution.Phase.ToString());
        summary.AddRow("Created", execution.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        summary.AddRow("Updated", execution.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();

        var eventsTable = new Table().Border(TableBorder.Rounded).AddColumns("Timestamp", "Phase", "Message");
        foreach (var executionEvent in events)
        {
            eventsTable.AddRow(
                executionEvent.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                executionEvent.Phase.ToString(),
                Markup.Escape(executionEvent.Message));
        }

        if (events.Count > 0)
        {
            AnsiConsole.Write(new Rule("Events"));
            AnsiConsole.Write(eventsTable);
            AnsiConsole.WriteLine();
        }

        if (plan is not null)
        {
            var planTable = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumns("Field", "Value");
            planTable.AddRow("Scope", plan.Scope.ToString());
            planTable.AddRow("Summary", Markup.Escape(plan.Summary));
            planTable.AddRow("Repositories", FormatList(plan.Repositories));
            planTable.AddRow("Validation", FormatList(plan.ValidationTargets));
            planTable.AddRow("Test Intent", FormatList(plan.TestIntent));
            planTable.AddRow("Risks", FormatList(plan.Risks));

            AnsiConsole.Write(new Rule("Plan"));
            AnsiConsole.Write(planTable);
            AnsiConsole.WriteLine();
        }

        if (sessions.Count > 0)
        {
            var sessionsTable = new Table().Border(TableBorder.Rounded).AddColumns("Role", "Provider", "Session Id", "Workdir", "Transcript");
            foreach (var session in sessions)
            {
                sessionsTable.AddRow(
                    session.Role.ToString(),
                    session.Provider.ToString(),
                    session.SessionId,
                    Markup.Escape(session.WorkingDirectory),
                    Markup.Escape(session.TranscriptPath));
            }

            AnsiConsole.Write(new Rule("Agent Sessions"));
            AnsiConsole.Write(sessionsTable);
            AnsiConsole.WriteLine();
        }

        if (agentOutput.Count > 0)
        {
            var outputTable = new Table().Border(TableBorder.Rounded).AddColumns("Session", "Role", "Provider", "Updated", "Excerpt");
            foreach (var sessionOutput in agentOutput
                         .GroupBy(chunk => chunk.SessionId)
                         .OrderByDescending(group => group.Max(item => item.Timestamp)))
            {
                var latest = sessionOutput.Last();
                outputTable.AddRow(
                    Markup.Escape(sessionOutput.Key),
                    latest.Role.ToString(),
                    latest.Provider.ToString(),
                    latest.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    Markup.Escape(string.Join(Environment.NewLine, sessionOutput.TakeLast(20).Select(chunk => chunk.Content))));
            }

            AnsiConsole.Write(new Rule("Agent Output"));
            AnsiConsole.Write(outputTable);
        }

        return 0;
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0
            ? "-"
            : Markup.Escape(string.Join(Environment.NewLine, values));
    }
}
