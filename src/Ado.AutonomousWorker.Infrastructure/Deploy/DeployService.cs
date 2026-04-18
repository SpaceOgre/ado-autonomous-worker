using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Deploy;

public sealed class DeployService(
    IAzureDevOpsClient adoClient,
    IOptions<DeployOptions> deployOptions,
    ILogger<DeployService> logger
)
{
    private readonly IAzureDevOpsClient _adoClient = adoClient;
    private readonly DeployOptions _options = deployOptions.Value;
    private readonly ILogger<DeployService> _logger = logger;

    private const string ReadyForTestState = "Ready for test";

    /// <summary>
    /// Triggers the selected pipelines on the configured branch or the given <paramref name="tag"/>.
    /// Pass <paramref name="triggerBackend"/> and/or <paramref name="triggerFrontend"/> to control which pipelines run.
    /// Returns the run IDs for triggered pipelines, or <c>null</c> for pipelines that were skipped.
    /// </summary>
    public async Task<(int? BackendRunId, int? FrontendRunId)> TriggerPipelinesAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken,
        string? tag = null,
        bool triggerBackend = true,
        bool triggerFrontend = true
    )
    {
        if (tag is not null && string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException("Tag must not be empty or whitespace when provided.", nameof(tag));
        }

        if (string.IsNullOrWhiteSpace(_options.Branch))
        {
            throw new InvalidOperationException(
                "Deploy configuration is invalid: Branch must not be empty or whitespace."
            );
        }

        var normalizedTag = tag?.Trim();
        var normalizedBranch = _options.Branch.Trim();

        var refName = normalizedTag is not null ? $"refs/tags/{normalizedTag}" : $"refs/heads/{normalizedBranch}";
        var refLabel = normalizedTag is not null ? $"tag '{normalizedTag}'" : $"branch '{normalizedBranch}'";
        int? backendRunId = null;
        int? frontendRunId = null;

        if (triggerBackend)
        {
            progress.Report($"Triggering backend pipeline (id={_options.BackendPipelineId}) on {refLabel}…");
            backendRunId = await _adoClient.TriggerPipelineAsync(
                _options.BackendPipelineId,
                refName,
                cancellationToken
            );
            progress.Report($"Backend run #{backendRunId} started.");
        }

        if (triggerFrontend)
        {
            progress.Report($"Triggering frontend pipeline (id={_options.FrontendPipelineId}) on {refLabel}…");
            frontendRunId = await _adoClient.TriggerPipelineAsync(
                _options.FrontendPipelineId,
                refName,
                cancellationToken
            );
            progress.Report($"Frontend run #{frontendRunId} started.");
        }

        return (backendRunId, frontendRunId);
    }

    /// <summary>
    /// Polls the triggered pipeline runs until they finish or time out.
    /// Pass <c>null</c> for a run ID to skip polling for that pipeline.
    /// Throws <see cref="InvalidOperationException"/> if any triggered run fails or times out.
    /// </summary>
    public async Task WaitForPipelinesAsync(
        int? backendRunId,
        int? frontendRunId,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        if (_options.TimeoutMinutes <= 0)
            throw new InvalidOperationException(
                $"Deploy configuration is invalid: TimeoutMinutes must be greater than 0 (currently {_options.TimeoutMinutes})."
            );
        if (_options.PollIntervalSeconds <= 0)
            throw new InvalidOperationException(
                $"Deploy configuration is invalid: PollIntervalSeconds must be greater than 0 (currently {_options.PollIntervalSeconds})."
            );

        var timeout = TimeSpan.FromMinutes(_options.TimeoutMinutes);
        var pollInterval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        var deadline = DateTimeOffset.UtcNow + timeout;

        var backendDone = backendRunId is null;
        var frontendDone = frontendRunId is null;

        while ((!backendDone || !frontendDone) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(pollInterval, cancellationToken);

            if (!backendDone)
            {
                var (state, result) = await _adoClient.GetPipelineRunStatusAsync(
                    _options.BackendPipelineId,
                    backendRunId!.Value,
                    cancellationToken
                );
                _logger.LogDebug("Backend run #{RunId}: state={State} result={Result}", backendRunId, state, result);

                if (IsFinished(state))
                {
                    backendDone = true;
                    if (!IsSuccessful(result))
                        throw new InvalidOperationException(
                            $"Backend pipeline run #{backendRunId} finished with result '{result}'. Deploy aborted."
                        );
                    progress.Report($"Backend pipeline run #{backendRunId} completed successfully.");
                }
                else
                {
                    progress.Report($"Backend run #{backendRunId}: {state}…");
                }
            }

            if (!frontendDone)
            {
                var (state, result) = await _adoClient.GetPipelineRunStatusAsync(
                    _options.FrontendPipelineId,
                    frontendRunId!.Value,
                    cancellationToken
                );
                _logger.LogDebug("Frontend run #{RunId}: state={State} result={Result}", frontendRunId, state, result);

                if (IsFinished(state))
                {
                    frontendDone = true;
                    if (!IsSuccessful(result))
                        throw new InvalidOperationException(
                            $"Frontend pipeline run #{frontendRunId} finished with result '{result}'. Deploy aborted."
                        );
                    progress.Report($"Frontend pipeline run #{frontendRunId} completed successfully.");
                }
                else
                {
                    progress.Report($"Frontend run #{frontendRunId}: {state}…");
                }
            }
        }

        if (!backendDone || !frontendDone)
            throw new InvalidOperationException(
                $"Pipeline runs did not complete within {_options.TimeoutMinutes} minutes."
            );
    }

    /// <summary>
    /// Computes the work item changes that would be applied (for dry-run or actual execution).
    /// Returns:
    ///   - <c>taskChanges</c>: Resolved tasks/bugs to move to Ready for test
    ///   - <c>storyChanges</c>: User stories whose children are all Ready for test or Closed
    /// </summary>
    public async Task<(
        IReadOnlyList<DeployWorkItemChange> TaskChanges,
        IReadOnlyList<DeployWorkItemChange> StoryChanges
    )> ComputeChangesAsync(CancellationToken cancellationToken)
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
                _options.ReadyForTestAssignee
            ))
            .ToArray();

        // 2. Active user stories where all children will be Ready for test or Closed after the changes above
        var resolvedIds = new HashSet<int>(resolvedItems.Select(i => i.WorkItemId));
        var stories = await _adoClient.QueryActiveSprintUserStoriesWithChildrenAsync(cancellationToken);

        var storyChanges = new List<DeployWorkItemChange>();

        // Precompute the union of all unknown child IDs across every story so we can
        // fetch their states in a single batched API call instead of one call per story.
        var allUnknownIds = stories
            .SelectMany(s => s.ChildIds)
            .Where(id => !resolvedIds.Contains(id))
            .Distinct()
            .ToArray();

        var childStateMap =
            allUnknownIds.Length > 0
                ? await _adoClient.GetWorkItemStatesAsync(allUnknownIds, cancellationToken)
                : (IReadOnlyDictionary<int, string>)new Dictionary<int, string>();

        foreach (var (story, childIds) in stories)
        {
            if (childIds.Count == 0)
                continue;

            var allDone = childIds.All(id =>
            {
                // This ID is being moved to Ready for test
                if (resolvedIds.Contains(id))
                    return true;
                // Already in an acceptable end state
                if (childStateMap.TryGetValue(id, out var state))
                    return IsAcceptableChildState(state);
                return false;
            });

            if (allDone)
            {
                storyChanges.Add(
                    new DeployWorkItemChange(
                        story.WorkItemId,
                        story.WorkItemType.ToString(),
                        story.Title,
                        story.State,
                        ReadyForTestState,
                        story.AssignedTo,
                        _options.ReadyForTestAssignee
                    )
                );
            }
        }

        return (taskChanges, storyChanges);
    }

    /// <summary>Applies the computed changes to Azure DevOps.</summary>
    public async Task ApplyChangesAsync(
        IReadOnlyList<DeployWorkItemChange> taskChanges,
        IReadOnlyList<DeployWorkItemChange> storyChanges,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        foreach (var change in taskChanges)
        {
            progress.Report(
                $"Updating {change.WorkItemType} #{change.WorkItemId} '{change.Title}' → {change.NewState}, assignee: {change.NewAssignee}"
            );
            await _adoClient.UpdateWorkItemStateAndAssigneeAsync(
                change.WorkItemId,
                change.NewState,
                change.NewAssignee,
                cancellationToken
            );
        }

        foreach (var change in storyChanges)
        {
            progress.Report(
                $"Updating User Story #{change.WorkItemId} '{change.Title}' → {change.NewState}, assignee: {change.NewAssignee}"
            );
            await _adoClient.UpdateWorkItemStateAndAssigneeAsync(
                change.WorkItemId,
                change.NewState,
                change.NewAssignee,
                cancellationToken
            );
        }
    }

    private static bool IsFinished(string state) =>
        string.Equals(state, "completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessful(string? result) =>
        string.Equals(result, "succeeded", StringComparison.OrdinalIgnoreCase);

    private static bool IsAcceptableChildState(string state) =>
        string.Equals(state, ReadyForTestState, StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "Closed", StringComparison.OrdinalIgnoreCase);
}
