using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Core.Services;

public interface IStateStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task SaveExecutionAsync(TaskExecutionRecord execution, CancellationToken cancellationToken);

    Task<TaskExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskExecutionRecord>> ListActiveExecutionsAsync(CancellationToken cancellationToken);

    Task UpdatePhaseAsync(string executionId, TaskPhase phase, CancellationToken cancellationToken);

    Task AppendEventAsync(ExecutionEventRecord executionEvent, CancellationToken cancellationToken);

    Task SaveAgentSessionAsync(string executionId, AgentSessionHandle session, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentSessionHandle>> GetAgentSessionsAsync(string executionId, CancellationToken cancellationToken);

    Task SaveReviewResultAsync(string executionId, ReviewResult reviewResult, CancellationToken cancellationToken);
}
