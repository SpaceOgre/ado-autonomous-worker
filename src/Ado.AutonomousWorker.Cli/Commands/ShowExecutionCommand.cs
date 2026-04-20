using System.ComponentModel;
using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Abstractions;
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

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        await stateStore.InitializeAsync(cancellationToken);

        var hasExecutionId = !string.IsNullOrWhiteSpace(settings.ExecutionId);
        if (settings.Last == hasExecutionId)
        {
            AnsiConsole.MarkupLine(
                "[red]Specify exactly one of:[/] [yellow]--id <execution-id>[/] or [yellow]--last[/]"
            );
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
        var usageRecords = await stateStore.GetAgentUsageAsync(execution.ExecutionId, cancellationToken);

        var summary = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumns("Field", "Value");
        summary.AddRow("Execution Id", execution.ExecutionId);
        summary.AddRow("Work Item", execution.WorkItemId.ToString());
        summary.AddRow("Title", Markup.Escape(execution.Title));
        summary.AddRow("Kind", CliStyle.QueueKind(execution.Kind));
        summary.AddRow("Phase", CliStyle.Phase(execution.Phase));
        summary.AddRow("Status", CliStyle.Status(execution.Status));
        summary.AddRow("Created", execution.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        summary.AddRow("Updated", execution.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        if (execution.PullRequests is { Count: > 0 })
            summary.AddRow("PRs", string.Join("  ", execution.PullRequests.Select(pr => $"!{pr.PullRequestId}")));
        if (execution.ChildTasks is { Count: > 0 })
            summary.AddRow(
                "Child Tasks",
                string.Join("  ", execution.ChildTasks.Select(t => $"#{t.WorkItemId} ({t.RepoKind})"))
            );

        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();

        var eventsTable = new Table().Border(TableBorder.Rounded).AddColumns("Timestamp", "Phase", "Message");
        foreach (var executionEvent in events)
        {
            eventsTable.AddRow(
                executionEvent.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                CliStyle.Phase(executionEvent.Phase),
                Markup.Escape(executionEvent.Message)
            );
        }

        if (events.Count > 0)
        {
            AnsiConsole.Write(CliStyle.SectionRule("Events", "deepskyblue1"));
            AnsiConsole.Write(eventsTable);
            AnsiConsole.WriteLine();
        }

        if (plan is not null)
        {
            var planTable = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumns("Field", "Value");
            planTable.AddRow("Scope", plan.Scope.ToString());
            planTable.AddRow("Summary", Markup.Escape(plan.Summary));
            planTable.AddRow("Repositories", FormatList(plan.Repositories));
            planTable.AddRow("Test Intent", FormatList(plan.TestIntent));
            planTable.AddRow("Risks", FormatList(plan.Risks));

            AnsiConsole.Write(CliStyle.SectionRule("Plan", "cornflowerblue"));
            AnsiConsole.Write(planTable);
            AnsiConsole.WriteLine();
        }

        if (sessions.Count > 0)
        {
            var sessionsTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumns("Role", "Provider", "Session Id", "Workdir", "Transcript");
            foreach (var session in sessions)
            {
                sessionsTable.AddRow(
                    CliStyle.Role(session.Role),
                    CliStyle.Provider(session.Provider),
                    session.SessionId,
                    Markup.Escape(session.WorkingDirectory),
                    Markup.Escape(session.TranscriptPath)
                );
            }

            AnsiConsole.Write(CliStyle.SectionRule("Agent Sessions", "mediumpurple2"));
            AnsiConsole.Write(sessionsTable);
            AnsiConsole.WriteLine();
        }

        if (agentOutput.Count > 0)
        {
            var outputTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumns("Session", "Role", "Provider", "Updated", "Excerpt");
            foreach (
                var sessionOutput in agentOutput
                    .GroupBy(chunk => chunk.SessionId)
                    .OrderByDescending(group => group.Max(item => item.Timestamp))
            )
            {
                var latest = sessionOutput.Last();
                outputTable.AddRow(
                    Markup.Escape(sessionOutput.Key),
                    CliStyle.Role(latest.Role),
                    CliStyle.Provider(latest.Provider),
                    latest.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    Markup.Escape(
                        string.Join(Environment.NewLine, sessionOutput.TakeLast(20).Select(chunk => chunk.Content))
                    )
                );
            }

            AnsiConsole.Write(CliStyle.SectionRule("Agent Output", "mediumspringgreen"));
            AnsiConsole.Write(outputTable);
        }

        if (usageRecords.Count > 0)
        {
            AnsiConsole.WriteLine();
            var usageTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumns("Phase", "Role", "Provider", "Model", "Tokens In", "Tokens Out", "Cached", "Cost");

            var totalIn = 0;
            var totalOut = 0;
            var totalCached = 0;
            var totalCost = 0m;

            foreach (var u in usageRecords)
            {
                usageTable.AddRow(
                    CliStyle.Phase(u.Phase),
                    CliStyle.Role(u.Role),
                    CliStyle.Provider(u.Provider),
                    Markup.Escape(u.ActualModel ?? "-"),
                    u.InputTokens.ToString("N0"),
                    u.OutputTokens.ToString("N0"),
                    u.CachedInputTokens > 0 ? u.CachedInputTokens.ToString("N0") : "-",
                    u.CostUsd.HasValue ? $"${u.CostUsd:F4}" : "-"
                );

                totalIn += u.InputTokens;
                totalOut += u.OutputTokens;
                totalCached += u.CachedInputTokens;
                totalCost += u.CostUsd ?? 0m;
            }

            usageTable.AddRow(
                "[bold]Total[/]",
                "",
                "",
                "",
                $"[bold]{totalIn:N0}[/]",
                $"[bold]{totalOut:N0}[/]",
                totalCached > 0 ? $"[bold]{totalCached:N0}[/]" : "-",
                totalCost > 0m ? $"[bold green]${totalCost:F4}[/]" : "-"
            );

            AnsiConsole.Write(CliStyle.SectionRule("Token Usage", "gold3_1"));
            AnsiConsole.Write(usageTable);
        }

        var relatedExecutions = await stateStore.ListExecutionsByWorkItemAsync(execution.WorkItemId, cancellationToken);
        var prRelated = await stateStore.ListRelatedExecutionsByPrAsync(execution.ExecutionId, cancellationToken);
        var childTaskRelated = await stateStore.ListRelatedExecutionsByChildTaskAsync(
            execution.ExecutionId,
            cancellationToken
        );
        var siblings = relatedExecutions
            .Concat(prRelated)
            .Concat(childTaskRelated)
            .Where(e => e.ExecutionId != execution.ExecutionId)
            .DistinctBy(e => e.ExecutionId)
            .OrderByDescending(e => e.UpdatedAt)
            .ToList();
        if (siblings.Count > 0)
        {
            AnsiConsole.WriteLine();
            var relatedTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumns("Execution Id", "Kind", "Phase", "Status", "Updated");

            foreach (var sibling in siblings)
            {
                relatedTable.AddRow(
                    sibling.ExecutionId,
                    CliStyle.QueueKind(sibling.Kind),
                    CliStyle.Phase(sibling.Phase),
                    CliStyle.Status(sibling.Status),
                    sibling.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                );
            }

            AnsiConsole.Write(CliStyle.SectionRule("Related Executions", "grey"));
            AnsiConsole.Write(relatedTable);
        }

        return 0;
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "-" : Markup.Escape(string.Join(Environment.NewLine, values));
    }
}
