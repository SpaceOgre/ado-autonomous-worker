using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Core.Abstractions;

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

    /// <summary>Returns QueueItems (Kind=AiPrFix) for PRs that have the "AI-Rework" label.</summary>
    Task<IReadOnlyList<QueueItem>> QueryAiPrFixAsync(CancellationToken cancellationToken);

    /// <summary>Returns a single QueueItem (Kind=AiPrFix) for the given PR ID, or null if the PR lacks required labels or has no linked work items.</summary>
    Task<QueueItem?> GetPrFixItemAsync(int pullRequestId, CancellationToken cancellationToken);

    /// <summary>Returns all review threads for a PR (caller filters active ones).</summary>
    Task<IReadOnlyList<PrReviewThread>> GetPullRequestThreadsAsync(string repositoryName, int pullRequestId, CancellationToken cancellationToken);

    /// <summary>Fetches the repository name and source branch for a PR by ID (used on resume to reconstruct PrFixContext).</summary>
    Task<(string RepositoryName, string BranchName)?> GetPullRequestLocationAsync(int pullRequestId, CancellationToken cancellationToken);

    /// <summary>Removes a label from a PR by name (case-insensitive).</summary>
    Task RemovePullRequestLabelAsync(string repositoryName, int pullRequestId, string labelName, CancellationToken cancellationToken);

    // ── Deploy / pipeline ──────────────────────────────────────────────────

    /// <summary>Verifies that a pipeline with the given ID exists. Returns the pipeline name on success, or null if not found.</summary>
    Task<string?> TryGetPipelineNameAsync(int pipelineId, CancellationToken cancellationToken);

    /// <summary>Triggers a pipeline run on the specified git ref (e.g. <c>refs/heads/main</c> or <c>refs/tags/v1.2.3</c>) and returns the run ID.</summary>
    Task<int> TriggerPipelineAsync(int pipelineId, string refName, CancellationToken cancellationToken);

    /// <summary>Gets the current state and result of a pipeline run.</summary>
    Task<(string State, string? Result)> GetPipelineRunStatusAsync(int pipelineId, int runId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns all Task and Bug work items in the current sprint whose state is "Resolved".
    /// </summary>
    Task<IReadOnlyList<WorkItemSummary>> QueryResolvedSprintItemsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns all active User Story work items in the current sprint together with their child work-item IDs.
    /// </summary>
    Task<IReadOnlyList<(WorkItemSummary Story, IReadOnlyList<int> ChildIds)>> QueryActiveSprintUserStoriesWithChildrenAsync(CancellationToken cancellationToken);

    /// <summary>Returns the state of the given work items keyed by ID.</summary>
    Task<IReadOnlyDictionary<int, string>> GetWorkItemStatesAsync(IReadOnlyList<int> workItemIds, CancellationToken cancellationToken);

    /// <summary>Updates both the state and assignee of a work item in a single PATCH call.</summary>
    Task UpdateWorkItemStateAndAssigneeAsync(int workItemId, string state, string assignee, CancellationToken cancellationToken);
}
