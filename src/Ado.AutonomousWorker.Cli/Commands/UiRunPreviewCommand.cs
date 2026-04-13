using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Infrastructure.Queue;
using Microsoft.Extensions.Options;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class UiRunPreviewCommand(
    QueueInspector queueInspector,
    RunPreambleDisplay runPreambleDisplay,
    IOptions<WorkerOptions> workerOptions) : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var eligibleQueue = await queueInspector.GetQueueAsync(cancellationToken);
        var selectedQueue = eligibleQueue
            .Take(workerOptions.Value.MaxItemsPerRun)
            .ToArray();

        runPreambleDisplay.ShowSplash("Run preview");
        runPreambleDisplay.ShowQueuePreview(selectedQueue, eligibleQueue.Count, workerOptions.Value.MaxItemsPerRun, previewOnly: true);
        return 0;
    }
}
