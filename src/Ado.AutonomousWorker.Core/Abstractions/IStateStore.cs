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

    Task<IReadOnlyList<TaskExecutionRecord>> ListExecutionsByWorkItemAsync(
        int workItemId,
        CancellationToken cancellationToken
    );

    Task UpdatePhaseAsync(string executionId, TaskPhase phase, CancellationToken cancellationToken);

    Task LinkPullRequestToExecutionAsync(
        string executionId,
        int pullRequestId,
        string? repositoryName,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<TaskExecutionRecord>> ListRelatedExecutionsByPrAsync(
        string executionId,
        CancellationToken cancellationToken
    );

    Task LinkChildTaskToExecutionAsync(
        string executionId,
        int workItemId,
        string repoKind,
        string title,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<TaskExecutionRecord>> ListRelatedExecutionsByChildTaskAsync(
        string executionId,
        CancellationToken cancellationToken
    );

    Task UpdateStatusAsync(string executionId, ExecutionStatus status, CancellationToken cancellationToken);

    Task AppendEventAsync(ExecutionEventRecord executionEvent, CancellationToken cancellationToken);

    Task SaveAgentSessionAsync(string executionId, AgentSessionHandle session, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentSessionHandle>> GetAgentSessionsAsync(
        string executionId,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<ExecutionEventRecord>> GetEventsAsync(string executionId, CancellationToken cancellationToken);

    Task AppendAgentOutputChunkAsync(string executionId, AgentOutputChunk chunk, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentOutputChunk>> GetAgentOutputChunksAsync(
        string executionId,
        CancellationToken cancellationToken
    );

    Task SavePlanAsync(string executionId, PlanResult planResult, CancellationToken cancellationToken);

    Task<PlanResult?> GetPlanAsync(string executionId, CancellationToken cancellationToken);

    Task SaveReviewResultAsync(string executionId, ReviewResult reviewResult, CancellationToken cancellationToken);

    Task<ReviewResult?> GetLatestReviewResultAsync(string executionId, CancellationToken cancellationToken);

    Task RecordAgentUsageAsync(
        string executionId,
        TaskPhase phase,
        AgentRole role,
        AgentProvider provider,
        AgentUsage usage,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<AgentUsageRecord>> GetAgentUsageAsync(string executionId, CancellationToken cancellationToken);

    // Deploy records
    Task SaveDeployAsync(DeployRecord deploy, CancellationToken cancellationToken);
    Task<DeployRecord?> GetDeployAsync(string deployId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeployRecord>> ListDeploysAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DeployRecord>> ListActiveDeploysAsync(CancellationToken cancellationToken);
    Task UpdateDeployPhaseAsync(string deployId, DeployPhase phase, CancellationToken cancellationToken);
    Task UpdateDeployRunIdsAsync(string deployId, int? backendRunId, int? frontendRunId, CancellationToken cancellationToken);
    Task UpdateDeployStatusAsync(string deployId, DeployStatus status, string? failureReason, CancellationToken cancellationToken);
    Task AppendDeployEventAsync(string deployId, string message, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeployEventRecord>> GetDeployEventsAsync(string deployId, CancellationToken cancellationToken);
    Task SaveDeployWorkItemChangesAsync(string deployId, IReadOnlyList<DeployWorkItemChange> changes, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeployWorkItemChange>> GetDeployWorkItemChangesAsync(string deployId, CancellationToken cancellationToken);
}
