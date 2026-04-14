using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Core.Abstractions;

public interface IStateStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task SaveExecutionAsync(TaskExecutionRecord execution, CancellationToken cancellationToken);

    Task<TaskExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskExecutionRecord>> ListActiveExecutionsAsync(CancellationToken cancellationToken);

    Task UpdatePhaseAsync(string executionId, TaskPhase phase, CancellationToken cancellationToken);

    Task UpdateStatusAsync(string executionId, ExecutionStatus status, CancellationToken cancellationToken);

    Task AppendEventAsync(ExecutionEventRecord executionEvent, CancellationToken cancellationToken);

    Task SaveAgentSessionAsync(string executionId, AgentSessionHandle session, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentSessionHandle>> GetAgentSessionsAsync(string executionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionEventRecord>> GetEventsAsync(string executionId, CancellationToken cancellationToken);

    Task AppendAgentOutputChunkAsync(string executionId, AgentOutputChunk chunk, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentOutputChunk>> GetAgentOutputChunksAsync(string executionId, CancellationToken cancellationToken);

    Task SavePlanAsync(string executionId, PlanResult planResult, CancellationToken cancellationToken);

    Task<PlanResult?> GetPlanAsync(string executionId, CancellationToken cancellationToken);

    Task SaveReviewResultAsync(string executionId, ReviewResult reviewResult, CancellationToken cancellationToken);

    Task<ReviewResult?> GetLatestReviewResultAsync(string executionId, CancellationToken cancellationToken);

    Task RecordAgentUsageAsync(string executionId, TaskPhase phase, AgentRole role, AgentProvider provider, AgentUsage usage, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentUsageRecord>> GetAgentUsageAsync(string executionId, CancellationToken cancellationToken);
}
