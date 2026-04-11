namespace Ado.AutonomousWorker.Core.Services;

public interface ITaskOrchestrator
{
    Task<IReadOnlyList<string>> RunQueueAsync(CancellationToken cancellationToken);

    Task<string> RunTaskAsync(int workItemId, CancellationToken cancellationToken);

    Task ResumeTaskAsync(string executionId, CancellationToken cancellationToken);
}
