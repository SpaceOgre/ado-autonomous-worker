using System.ComponentModel;
using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Queue;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class RunCommand(
    ITaskOrchestrator taskOrchestrator,
    QueueInspector queueInspector,
    ExecutionLiveDisplay liveDisplay,
    IOptions<WorkerOptions> workerOptions
) : AsyncCommand<RunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description(
            "Override the iteration path used for backlog items (e.g. 'MyProject\\Sprint 5'). Has no effect on items already assigned to a sprint."
        )]
        [CommandOption("--iteration <ITERATION_PATH>")]
        public string? Iteration { get; init; }

        [Description("Only process PR-fix items (AI-Rework PRs), skipping regular AI-Work and AI-Rework backlog items.")]
        [CommandOption("--pr-fix-only")]
        public bool PrFixOnly { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var eligibleQueue = await queueInspector.GetQueueAsync(cancellationToken);
        var filtered = settings.PrFixOnly
            ? eligibleQueue.Where(i => i.Kind == QueueItemKind.AiPrFix).ToList()
            : eligibleQueue;
        var queue = filtered.Take(workerOptions.Value.MaxItemsPerRun).ToArray();

        RunPreambleDisplay.ShowQueuePreview(
            queue,
            filtered.Count,
            workerOptions.Value.MaxItemsPerRun,
            previewOnly: false
        );

        if (queue.Length == 0)
        {
            return 0;
        }

        if (AnsiConsole.Profile.Capabilities.Interactive && !AnsiConsole.Confirm("Proceed with run?"))
        {
            return 0;
        }

        var executionIds = new List<string>(queue.Length);
        for (var index = 0; index < queue.Length; index++)
        {
            var item = queue[index];
            var title = $"Queue {index + 1}/{queue.Length} - Work item #{item.WorkItemId}";
            var executionId = await liveDisplay.RunForWorkItemAsync(
                title,
                item.WorkItemId,
                token => taskOrchestrator.RunTaskAsync(item, settings.Iteration, token),
                cancellationToken
            );

            executionIds.Add(executionId);
        }

        AnsiConsole.MarkupLine($"[green]Completed queue run.[/] Executions: {string.Join(", ", executionIds)}");
        return 0;
    }
}
