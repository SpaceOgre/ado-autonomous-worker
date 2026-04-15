using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Deploy;

public sealed class DeployService(
    IAzureDevOpsClient adoClient,
    IOptions<DeployOptions> deployOptions,
    ILogger<DeployService> logger)
{
    private readonly IAzureDevOpsClient _adoClient = adoClient;
    private readonly DeployOptions _options = deployOptions.Value;
    private readonly ILogger<DeployService> _logger = logger;

    private const string ReadyForTestState = "Ready for Test";

    /// <summary>
    /// Triggers the backend and frontend pipelines and waits for both to complete successfully.
    /// Returns the pipeline run URLs for display.
    /// </summary>
    public async Task<(int BackendRunId, int FrontendRunId)> TriggerPipelinesAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        progress.Report($"Triggering backend pipeline (id={_options.BackendPipelineId}) on branch '{_options.Branch}'…");
        var backendRunId = await _adoClient.TriggerPipelineAsync(_options.BackendPipelineId, _options.Branch, cancellationToken);
        progress.Report($"Backend run #{backendRunId} started.");

        progress.Report($"Triggering frontend pipeline (id={_options.FrontendPipelineId}) on branch '{_options.Branch}'…");
        var frontendRunId = await _adoClient.TriggerPipelineAsync(_options.FrontendPipelineId, _options.Branch, cancellationToken);
        progress.Report($"Frontend run #{frontendRunId} started.");

        return (backendRunId, frontendRunId);
    }

    /// <summary>
    /// Polls both pipeline runs until they finish or time out.
    /// Throws <see cref="InvalidOperationException"/> if either run fails.
    /// </summary>
    public async Task WaitForPipelinesAsync(
        int backendRunId,
        int frontendRunId,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(_options.TimeoutMinutes);
        var pollInterval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        var deadline = DateTimeOffset.UtcNow + timeout;

        var backendDone = false;
        var frontendDone = false;

        while ((!backendDone || !frontendDone) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(pollInterval, cancellationToken);

            if (!backendDone)
            {
                var (state, result) = await _adoClient.GetPipelineRunStatusAsync(_options.BackendPipelineId, backendRunId, cancellationToken);
                _logger.LogDebug("Backend run #{RunId}: state={State} result={Result}", backendRunId, state, result);

                if (IsFinished(state))
                {
                    backendDone = true;
                    if (!IsSuccessful(result))
                        throw new InvalidOperationException($"Backend pipeline run #{backendRunId} finished with result '{result}'. Deploy aborted.");
                    progress.Report($"Backend pipeline run #{backendRunId} completed successfully.");
                }
                else
                {
                    progress.Report($"Backend run #{backendRunId}: {state}…");
                }
            }

            if (!frontendDone)
            {
                var (state, result) = await _adoClient.GetPipelineRunStatusAsync(_options.FrontendPipelineId, frontendRunId, cancellationToken);
                _logger.LogDebug("Frontend run #{RunId}: state={State} result={Result}", frontendRunId, state, result);

                if (IsFinished(state))
                {
                    frontendDone = true;
                    if (!IsSuccessful(result))
                        throw new InvalidOperationException($"Frontend pipeline run #{frontendRunId} finished with result '{result}'. Deploy aborted.");
                    progress.Report($"Frontend pipeline run #{frontendRunId} completed successfully.");
                }
                else
                {
                    progress.Report($"Frontend run #{frontendRunId}: {state}…");
                }
            }
        }

        if (!backendDone || !frontendDone)
            throw new InvalidOperationException($"Pipeline runs did not complete within {_options.TimeoutMinutes} minutes.");
    }

    /// <summary>
    /// Computes the work item changes that would be applied (for dry-run or actual execution).
    /// Returns:
    ///   - <c>taskChanges</c>: Resolved tasks/bugs to move to Ready for Test
    ///   - <c>storyChanges</c>: User stories whose children are all Ready for Test or Closed
    /// </summary>
    public async Task<(IReadOnlyList<DeployWorkItemChange> TaskChanges, IReadOnlyList<DeployWorkItemChange> StoryChanges)> ComputeChangesAsync(
        CancellationToken cancellationToken)
    {
        // 1. Resolved tasks/bugs in current sprint
        var resolvedItems = await _adoClient.QueryResolvedSprintItemsAsync(cancellationToken);
        var taskChanges = resolvedItems
            .Select(item => new DeployWorkItemChange(
                item.WorkItemId,
                item.WorkItemType.ToString(),
                item.Title,
                item.State,
                ReadyForTestState,
                item.AssignedTo,
                _options.ReadyForTestAssignee))
            .ToArray();

        // 2. Active user stories where all children will be Ready for Test or Closed after the changes above
        var resolvedIds = new HashSet<int>(resolvedItems.Select(i => i.WorkItemId));
        var stories = await _adoClient.QueryActiveSprintUserStoriesWithChildrenAsync(cancellationToken);

        var storyChanges = new List<DeployWorkItemChange>();

        foreach (var (story, childIds) in stories)
        {
            if (childIds.Count == 0)
                continue;

            // Get current states for children we don't already know about
            var unknownIds = childIds.Where(id => !resolvedIds.Contains(id)).ToArray();
            IReadOnlyDictionary<int, string> childStates;

            if (unknownIds.Length > 0)
            {
                childStates = await _adoClient.GetWorkItemStatesAsync(unknownIds, cancellationToken);
            }
            else
            {
                childStates = new Dictionary<int, string>();
            }

            var allDone = childIds.All(id =>
            {
                // This ID is being moved to Ready for Test
                if (resolvedIds.Contains(id))
                    return true;
                // Already in an acceptable end state
                if (childStates.TryGetValue(id, out var state))
                    return IsAcceptableChildState(state);
                return false;
            });

            if (allDone)
            {
                storyChanges.Add(new DeployWorkItemChange(
                    story.WorkItemId,
                    story.WorkItemType.ToString(),
                    story.Title,
                    story.State,
                    ReadyForTestState,
                    story.AssignedTo,
                    _options.ReadyForTestAssignee));
            }
        }

        return (taskChanges, storyChanges);
    }

    /// <summary>Applies the computed changes to Azure DevOps.</summary>
    public async Task ApplyChangesAsync(
        IReadOnlyList<DeployWorkItemChange> taskChanges,
        IReadOnlyList<DeployWorkItemChange> storyChanges,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        foreach (var change in taskChanges)
        {
            progress.Report($"Updating {change.WorkItemType} #{change.WorkItemId} '{change.Title}' → {change.NewState}, assignee: {change.NewAssignee}");
            await _adoClient.UpdateWorkItemStateAndAssigneeAsync(change.WorkItemId, change.NewState, change.NewAssignee, cancellationToken);
        }

        foreach (var change in storyChanges)
        {
            progress.Report($"Updating User Story #{change.WorkItemId} '{change.Title}' → {change.NewState}, assignee: {change.NewAssignee}");
            await _adoClient.UpdateWorkItemStateAndAssigneeAsync(change.WorkItemId, change.NewState, change.NewAssignee, cancellationToken);
        }
    }

    private static bool IsFinished(string state) =>
        string.Equals(state, "completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessful(string? result) =>
        string.Equals(result, "succeeded", StringComparison.OrdinalIgnoreCase);

    private static bool IsAcceptableChildState(string state) =>
        string.Equals(state, ReadyForTestState, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, "Closed", StringComparison.OrdinalIgnoreCase);
}
