namespace Ado.AutonomousWorker.Core.Abstractions;

public interface ITaskOrchestrator
{
    Task<IReadOnlyList<string>> RunQueueAsync(CancellationToken cancellationToken);

    Task<string> RunTaskAsync(int workItemId, CancellationToken cancellationToken);

    Task ResumeTaskAsync(string executionId, CancellationToken cancellationToken);
}
