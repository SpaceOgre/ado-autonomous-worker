using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Infrastructure.Queue;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class RunCommand(
    ITaskOrchestrator taskOrchestrator,
    QueueInspector queueInspector,
    ExecutionLiveDisplay liveDisplay,
    IOptions<WorkerOptions> workerOptions) : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var eligibleQueue = await queueInspector.GetQueueAsync(cancellationToken);
        var queue = eligibleQueue
            .Take(workerOptions.Value.MaxItemsPerRun)
            .ToArray();

        RunPreambleDisplay.ShowSplash("Queue run");
        RunPreambleDisplay.ShowQueuePreview(queue, eligibleQueue.Count, workerOptions.Value.MaxItemsPerRun, previewOnly: false);

        if (queue.Length == 0)
        {
            return 0;
        }

        if (AnsiConsole.Profile.Capabilities.Interactive && !AnsiConsole.Confirm("Proceed with run?"))
        {
            return 0;
        }

        if (AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.Clear();
        }

        RunPreambleDisplay.ShowRunHeader(queue, eligibleQueue.Count, workerOptions.Value.MaxItemsPerRun);

        var executionIds = new List<string>(queue.Length);
        for (var index = 0; index < queue.Length; index++)
        {
            var item = queue[index];
            RunPreambleDisplay.BeginLiveRunScreen(index + 1, queue.Length, eligibleQueue.Count, workerOptions.Value.MaxItemsPerRun, item);
            var title = $"Queue {index + 1}/{queue.Length} - Work item #{item.WorkItemId}";
            var executionId = await liveDisplay.RunForWorkItemAsync(
                title,
                item.WorkItemId,
                token => taskOrchestrator.RunTaskAsync(item.WorkItemId, token),
                cancellationToken);

            executionIds.Add(executionId);
        }

        AnsiConsole.MarkupLine($"[green]Completed queue run.[/] Executions: {string.Join(", ", executionIds)}");
        return 0;
    }
}
