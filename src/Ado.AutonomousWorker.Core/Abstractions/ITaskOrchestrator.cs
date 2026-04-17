using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Core.Abstractions;

public interface ITaskOrchestrator
{
    Task<string> RunTaskAsync(QueueItem item, string? iterationOverride, CancellationToken cancellationToken);

    Task<string> RunTaskAsync(int workItemId, string? iterationOverride, CancellationToken cancellationToken);

    Task<string> RunPrFixAsync(int pullRequestId, CancellationToken cancellationToken);

    Task ResumeTaskAsync(string executionId, CancellationToken cancellationToken);
}
