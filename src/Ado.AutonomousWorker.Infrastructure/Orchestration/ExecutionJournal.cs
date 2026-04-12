using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Services;
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
