using Ado.AutonomousWorker.Cli.Completion;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Tests;

public sealed class CliCompletionServiceTests
{
    [Test]
    public async Task GetSuggestionsAsync_SuggestsTopLevelCommands()
    {
        var service = new CliCompletionService(new FakeStateStore());

        var suggestions = await service.GetSuggestionsAsync(string.Empty, [], CancellationToken.None);

        await Assert.That(suggestions).Contains("ui");
        await Assert.That(suggestions).Contains("version");
        await Assert.That(suggestions).Contains("execution");
        await Assert.That(suggestions).Contains("executions");
    }

    [Test]
    public async Task GetSuggestionsAsync_SuggestsExecutionIdsForExecutionShow()
    {
        var service = new CliCompletionService(
            new FakeStateStore([
                new TaskExecutionRecord(
                    "exec-123",
                    1,
                    "Title",
                    QueueItemKind.AiWork,
                    TaskPhase.Publishing,
                    ExecutionStatus.Completed,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                ),
                new TaskExecutionRecord(
                    "exec-456",
                    2,
                    "Other",
                    QueueItemKind.AiRework,
                    TaskPhase.Reviewing,
                    ExecutionStatus.Failed,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                ),
            ])
        );

        var suggestions = await service.GetSuggestionsAsync(
            "exec-",
            ["execution", "show", "--id"],
            CancellationToken.None
        );

        await Assert.That(suggestions).IsEquivalentTo(new[] { "exec-123", "exec-456" });
    }

    [Test]
    public async Task GetSuggestionsAsync_SuggestsLastForTaskResume()
    {
        var service = new CliCompletionService(new FakeStateStore());

        var suggestions = await service.GetSuggestionsAsync("--", ["task", "resume"], CancellationToken.None);

        await Assert.That(suggestions).Contains("--execution-id");
        await Assert.That(suggestions).Contains("--last");
    }

    [Test]
    public async Task GetSuggestionsAsync_SuggestsUiBranchCommands()
    {
        var service = new CliCompletionService(new FakeStateStore());

        var suggestions = await service.GetSuggestionsAsync("r", ["ui"], CancellationToken.None);

        await Assert.That(suggestions).Contains("run-preview");
        await Assert.That(suggestions).DoesNotContain("splash");
    }

    [Test]
    public async Task GetSuggestionsAsync_SuggestsUiSplashOptions()
    {
        var service = new CliCompletionService(new FakeStateStore());

        var suggestions = await service.GetSuggestionsAsync("--", ["ui", "splash"], CancellationToken.None);

        await Assert.That(suggestions).IsEquivalentTo(new[] { "--status" });
    }

    [Test]
    public async Task GetSuggestionsAsync_SuggestsDashboardTopLevel()
    {
        var service = new CliCompletionService(new FakeStateStore());

        var suggestions = await service.GetSuggestionsAsync(string.Empty, [], CancellationToken.None);

        await Assert.That(suggestions).Contains("dashboard");
    }

    [Test]
    public async Task GetSuggestionsAsync_SuggestsDashboardOptions()
    {
        var service = new CliCompletionService(new FakeStateStore());

        var suggestions = await service.GetSuggestionsAsync("--", ["dashboard"], CancellationToken.None);

        await Assert.That(suggestions).Contains("--port");
        await Assert.That(suggestions).Contains("--no-open");
    }

    private sealed class FakeStateStore(IReadOnlyList<TaskExecutionRecord>? executions = null) : IStateStore
    {
        private readonly IReadOnlyList<TaskExecutionRecord> _executions = executions ?? [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveExecutionAsync(TaskExecutionRecord execution, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<TaskExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken) =>
            Task.FromResult(_executions.FirstOrDefault(item => item.ExecutionId == executionId));

        public Task<IReadOnlyList<TaskExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_executions);

        public Task<IReadOnlyList<TaskExecutionRecord>> ListActiveExecutionsAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult(_executions);

        public Task UpdatePhaseAsync(string executionId, TaskPhase phase, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UpdateStatusAsync(
            string executionId,
            ExecutionStatus status,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public Task AppendEventAsync(ExecutionEventRecord executionEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SaveAgentSessionAsync(
            string executionId,
            AgentSessionHandle session,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentSessionHandle>> GetAgentSessionsAsync(
            string executionId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<AgentSessionHandle>>([]);

        public Task<IReadOnlyList<ExecutionEventRecord>> GetEventsAsync(
            string executionId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<ExecutionEventRecord>>([]);

        public Task AppendAgentOutputChunkAsync(
            string executionId,
            AgentOutputChunk chunk,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentOutputChunk>> GetAgentOutputChunksAsync(
            string executionId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<AgentOutputChunk>>([]);

        public Task SavePlanAsync(string executionId, PlanResult planResult, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<PlanResult?> GetPlanAsync(string executionId, CancellationToken cancellationToken) =>
            Task.FromResult<PlanResult?>(null);

        public Task SaveReviewResultAsync(
            string executionId,
            ReviewResult reviewResult,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public Task<ReviewResult?> GetLatestReviewResultAsync(
            string executionId,
            CancellationToken cancellationToken
        ) => Task.FromResult<ReviewResult?>(null);

        public Task RecordAgentUsageAsync(
            string executionId,
            TaskPhase phase,
            AgentRole role,
            AgentProvider provider,
            AgentUsage usage,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentUsageRecord>> GetAgentUsageAsync(
            string executionId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<AgentUsageRecord>>([]);

        public Task<IReadOnlyList<TaskExecutionRecord>> ListExecutionsByWorkItemAsync(
            int workItemId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<TaskExecutionRecord>>(
                _executions.Where(e => e.WorkItemId == workItemId).ToList()
            );

        public Task LinkPullRequestToExecutionAsync(
            string executionId,
            int pullRequestId,
            string? repositoryName,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public Task<IReadOnlyList<TaskExecutionRecord>> ListRelatedExecutionsByPrAsync(
            string executionId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<TaskExecutionRecord>>([]);
    }
}
