using Ado.AutonomousWorker.Cli.Services;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Services;
using Ado.AutonomousWorker.Infrastructure.Services;
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
        var queue = (await queueInspector.GetQueueAsync(cancellationToken))
            .Take(workerOptions.Value.MaxItemsPerRun)
            .ToArray();

        if (queue.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No eligible queue items found.[/]");
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
                token => taskOrchestrator.RunTaskAsync(item.WorkItemId, token),
                cancellationToken);

            executionIds.Add(executionId);
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine($"[green]Completed queue run.[/] Executions: {string.Join(", ", executionIds)}");
        return 0;
    }
}
