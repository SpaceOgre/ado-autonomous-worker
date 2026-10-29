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
    /// Triggers one run of the deploy pipeline on the configured branch or the given <paramref name="tag"/>,
    /// deploying the halves selected by <paramref name="deployBackend"/> and <paramref name="deployFrontend"/>
    /// to the configured environment. Returns the run ID.
    /// </summary>
    public async Task<int> TriggerPipelineAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken,
        string? tag = null,
        bool deployBackend = true,
        bool deployFrontend = true
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

        if (!deployBackend && !deployFrontend)
        {
            throw new ArgumentException("At least one of the backend and frontend must be deployed.");
        }

        var normalizedTag = tag?.Trim();
        var normalizedBranch = _options.Branch.Trim();

        var refName = normalizedTag is not null ? $"refs/tags/{normalizedTag}" : $"refs/heads/{normalizedBranch}";
        var refLabel = normalizedTag is not null ? $"tag '{normalizedTag}'" : $"branch '{normalizedBranch}'";
        var templateParameters = new Dictionary<string, string>
        {
            ["environment"] = _options.Environment,
            ["deployBackend"] = deployBackend ? "true" : "false",
            ["deployFrontend"] = deployFrontend ? "true" : "false",
        };

        progress.Report(
            $"Triggering deploy pipeline (id={_options.PipelineId}) for {DescribeTargets(deployBackend, deployFrontend)} to {_options.Environment} on {refLabel}…"
        );
        var runId = await _adoClient.TriggerPipelineAsync(
            _options.PipelineId,
            refName,
            templateParameters,
            cancellationToken
        );
        progress.Report($"Deploy run #{runId} started.");

        return runId;
    }

    /// <summary>
    /// Polls the deploy pipeline run until it finishes or times out.
    /// Throws <see cref="InvalidOperationException"/> if the run fails or times out.
    /// </summary>
    public async Task WaitForPipelineAsync(int runId, IProgress<string> progress, CancellationToken cancellationToken)
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

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(pollInterval, cancellationToken);

            var (state, result) = await _adoClient.GetPipelineRunStatusAsync(
                _options.PipelineId,
                runId,
                cancellationToken
            );
            _logger.LogDebug("Deploy run #{RunId}: state={State} result={Result}", runId, state, result);

            if (IsFinished(state))
            {
                if (!IsSuccessful(result))
                    throw new InvalidOperationException(
                        $"Deploy pipeline run #{runId} finished with result '{result}'. Deploy aborted."
                    );
                progress.Report($"Deploy pipeline run #{runId} completed successfully.");
                return;
            }

            progress.Report($"Deploy run #{runId}: {state}…");
        }

        throw new InvalidOperationException($"Pipeline run did not complete within {_options.TimeoutMinutes} minutes.");
    }

    public static string DescribeTargets(bool deployBackend, bool deployFrontend) =>
        (deployBackend, deployFrontend) switch
        {
            (true, true) => "backend + frontend",
            (true, false) => "backend only",
            (false, true) => "frontend only",
            _ => "nothing",
        };

    /// <summary>
    /// Computes the work item changes that would be applied (for dry-run or actual execution).
    /// Returns:
    ///   - <c>taskChanges</c>: Resolved tasks/bugs to move to Ready for test
    ///   - <c>storyChanges</c>: Resolved user stories to move to Ready for test
    /// </summary>
    public async Task<(
        IReadOnlyList<DeployWorkItemChange> TaskChanges,
        IReadOnlyList<DeployWorkItemChange> StoryChanges
    )> ComputeChangesAsync(CancellationToken cancellationToken)
    {
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

        var resolvedStories = await _adoClient.QueryResolvedSprintUserStoriesAsync(cancellationToken);
        var storyChanges = resolvedStories
            .Select(story => new DeployWorkItemChange(
                story.WorkItemId,
                story.WorkItemType.ToString(),
                story.Title,
                story.State,
                ReadyForTestState,
                story.AssignedTo,
                _options.ReadyForTestAssignee
            ))
            .ToArray();

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
}
