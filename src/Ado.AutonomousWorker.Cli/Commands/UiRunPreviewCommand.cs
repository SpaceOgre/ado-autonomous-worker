using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Infrastructure.Queue;
using Microsoft.Extensions.Options;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class UiRunPreviewCommand(QueueInspector queueInspector, IOptions<WorkerOptions> workerOptions)
    : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var eligibleQueue = await queueInspector.GetQueueAsync(cancellationToken);
        var selectedQueue = eligibleQueue.Take(workerOptions.Value.MaxItemsPerRun).ToArray();

        RunPreambleDisplay.ShowQueuePreview(
            selectedQueue,
            eligibleQueue.Count,
            workerOptions.Value.MaxItemsPerRun,
            previewOnly: true
        );
        return 0;
    }
}
