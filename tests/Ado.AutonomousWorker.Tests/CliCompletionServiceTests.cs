using Ado.AutonomousWorker.Cli.Completion;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Abstractions;

namespace Ado.AutonomousWorker.Tests;

public sealed class CliCompletionServiceTests
{
    [Fact]
    public async Task GetSuggestionsAsync_SuggestsTopLevelCommands()
    {
        var service = new CliCompletionService(new FakeStateStore());

        var suggestions = await service.GetSuggestionsAsync("ex", [], CancellationToken.None);

        Assert.Contains("execution", suggestions);
        Assert.Contains("executions", suggestions);
    }

    [Fact]
    public async Task GetSuggestionsAsync_SuggestsExecutionIdsForExecutionShow()
    {
        var service = new CliCompletionService(new FakeStateStore(
        [
            new TaskExecutionRecord("exec-123", 1, "Title", QueueItemKind.AiWork, TaskPhase.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new TaskExecutionRecord("exec-456", 2, "Other", QueueItemKind.AiRework, TaskPhase.Failed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        ]));

        var suggestions = await service.GetSuggestionsAsync("exec-", ["execution", "show", "--id"], CancellationToken.None);

        Assert.Equal(["exec-123", "exec-456"], suggestions);
    }

    [Fact]
    public async Task GetSuggestionsAsync_SuggestsLastForTaskResume()
    {
        var service = new CliCompletionService(new FakeStateStore());

        var suggestions = await service.GetSuggestionsAsync("--", ["task", "resume"], CancellationToken.None);

        Assert.Contains("--execution-id", suggestions);
        Assert.Contains("--last", suggestions);
    }

    private sealed class FakeStateStore(IReadOnlyList<TaskExecutionRecord>? executions = null) : IStateStore
    {
        private readonly IReadOnlyList<TaskExecutionRecord> _executions = executions ?? [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveExecutionAsync(TaskExecutionRecord execution, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TaskExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken)
            => Task.FromResult(_executions.FirstOrDefault(item => item.ExecutionId == executionId));

        public Task<IReadOnlyList<TaskExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken)
            => Task.FromResult(_executions);

        public Task<IReadOnlyList<TaskExecutionRecord>> ListActiveExecutionsAsync(CancellationToken cancellationToken)
            => Task.FromResult(_executions);

        public Task UpdatePhaseAsync(string executionId, TaskPhase phase, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AppendEventAsync(ExecutionEventRecord executionEvent, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveAgentSessionAsync(string executionId, AgentSessionHandle session, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentSessionHandle>> GetAgentSessionsAsync(string executionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AgentSessionHandle>>([]);

        public Task<IReadOnlyList<ExecutionEventRecord>> GetEventsAsync(string executionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ExecutionEventRecord>>([]);

        public Task AppendAgentOutputChunkAsync(string executionId, AgentOutputChunk chunk, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentOutputChunk>> GetAgentOutputChunksAsync(string executionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AgentOutputChunk>>([]);

        public Task SavePlanAsync(string executionId, PlanResult planResult, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PlanResult?> GetPlanAsync(string executionId, CancellationToken cancellationToken)
            => Task.FromResult<PlanResult?>(null);

        public Task SaveReviewResultAsync(string executionId, ReviewResult reviewResult, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
