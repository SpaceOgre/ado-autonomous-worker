using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Core.Services;

public interface IAzureDevOpsClient
{
    Task<string?> GetCurrentSprintPathAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkItemSummary>> QueryAiWorkAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkItemSummary>> QueryAiReworkAsync(CancellationToken cancellationToken);

    Task<WorkItemDetails> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken);

    Task UpdateWorkItemStateAsync(int workItemId, string state, CancellationToken cancellationToken);

    Task AssignWorkItemAsync(int workItemId, string assignee, CancellationToken cancellationToken);

    Task UpdateIterationPathAsync(int workItemId, string iterationPath, CancellationToken cancellationToken);

    Task UpdateTagsAsync(int workItemId, IReadOnlyCollection<string> tags, CancellationToken cancellationToken);

    Task<int> CreateChildTaskAsync(int parentWorkItemId, string title, IReadOnlyCollection<string> tags, string iterationPath, string assignee, CancellationToken cancellationToken);

    Task<PullRequestInfo> CreateDraftPullRequestAsync(string repositoryName, string sourceBranch, string targetBranch, string title, string description, CancellationToken cancellationToken);

    Task LinkPullRequestAsync(int workItemId, PullRequestInfo pullRequest, CancellationToken cancellationToken);

    Task AddCommentAsync(int workItemId, string comment, CancellationToken cancellationToken);
}
