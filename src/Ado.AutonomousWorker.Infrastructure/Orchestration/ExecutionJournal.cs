using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal sealed class ExecutionJournal(
    IStateStore stateStore,
    IOptions<StorageOptions> storageOptions,
    ILogger<ExecutionJournal> logger)
{
    public async Task UpdatePhaseAsync(string executionId, TaskPhase phase, string message, CancellationToken cancellationToken)
    {
        await stateStore.UpdatePhaseAsync(executionId, phase, cancellationToken);
        await AppendEventAsync(executionId, phase, message, cancellationToken);
        logger.LogInformation("Execution {ExecutionId} [{Phase}] {Message}", executionId, phase, message);
    }

    /// <summary>
    /// Records a terminal outcome (Completed, Failed, or Skipped) against the last active workflow phase.
    /// The phase column is not changed — it retains the phase where execution stopped.
    /// </summary>
    public async Task TerminateAsync(string executionId, ExecutionStatus status, TaskPhase lastPhase, string message, CancellationToken cancellationToken)
    {
        await stateStore.UpdateStatusAsync(executionId, status, cancellationToken);
        await AppendEventAsync(executionId, lastPhase, message, cancellationToken);
        logger.LogInformation("Execution {ExecutionId} [{Status}@{Phase}] {Message}", executionId, status, lastPhase, message);
    }

    public async Task AppendEventAsync(string executionId, TaskPhase phase, string message, CancellationToken cancellationToken)
    {
        await stateStore.AppendEventAsync(new ExecutionEventRecord(executionId, DateTimeOffset.UtcNow, phase, message), cancellationToken);
        await AppendRunLogAsync(executionId, phase, message, cancellationToken);
    }

    public Func<AgentOutputChunk, CancellationToken, Task> CreateOutputObserver(string executionId)
    {
        return (chunk, cancellationToken) => stateStore.AppendAgentOutputChunkAsync(executionId, chunk, cancellationToken);
    }

    private async Task AppendRunLogAsync(string executionId, TaskPhase phase, string message, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(storageOptions.Value.LogRoot);
        var path = Path.Combine(storageOptions.Value.LogRoot, $"{executionId}.log.md");
        await File.AppendAllTextAsync(path, $"- {DateTimeOffset.UtcNow:O} [{phase}] {message}{Environment.NewLine}", cancellationToken);
    }
}
