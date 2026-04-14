using Ado.AutonomousWorker.Cli.Completion;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Tests;

public sealed class CliCompletionServiceTests
{
    [Fact]
    public async Task GetSuggestionsAsync_SuggestsTopLevelCommands()
    {
        var service = new CliCompletionService(new FakeStateStore());

        var suggestions = await service.GetSuggestionsAsync(string.Empty, [], CancellationToken.None);

        Assert.Contains("ui", suggestions);
        Assert.Contains("version", suggestions);
        Assert.Contains("execution", suggestions);
        Assert.Contains("executions", suggestions);
    }

    [Fact]
    public async Task GetSuggestionsAsync_SuggestsExecutionIdsForExecutionShow()
    {
        var service = new CliCompletionService(new FakeStateStore(
        [
            new TaskExecutionRecord("exec-123", 1, "Title", QueueItemKind.AiWork, TaskPhase.Publishing, ExecutionStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new TaskExecutionRecord("exec-456", 2, "Other", QueueItemKind.AiRework, TaskPhase.Reviewing, ExecutionStatus.Failed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
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

    [Fact]
    public async Task GetSuggestionsAsync_SuggestsUiBranchCommands()
    {
        var service = new CliCompletionService(new FakeStateStore());

        var suggestions = await service.GetSuggestionsAsync("r", ["ui"], CancellationToken.None);

        Assert.Contains("run-preview", suggestions);
        Assert.DoesNotContain("splash", suggestions);
    }

    [Fact]
    public async Task GetSuggestionsAsync_SuggestsUiSplashOptions()
    {
        var service = new CliCompletionService(new FakeStateStore());

        var suggestions = await service.GetSuggestionsAsync("--", ["ui", "splash"], CancellationToken.None);

        Assert.Equal(["--status"], suggestions);
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

        public Task UpdateStatusAsync(string executionId, ExecutionStatus status, CancellationToken cancellationToken) => Task.CompletedTask;

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

        public Task<ReviewResult?> GetLatestReviewResultAsync(string executionId, CancellationToken cancellationToken)
            => Task.FromResult<ReviewResult?>(null);

        public Task RecordAgentUsageAsync(string executionId, TaskPhase phase, AgentRole role, AgentProvider provider, AgentUsage usage, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentUsageRecord>> GetAgentUsageAsync(string executionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AgentUsageRecord>>([]);
    }
}
