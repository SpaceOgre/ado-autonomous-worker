using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal sealed class TaskOrchestrator(
    IAzureDevOpsClient azureDevOpsClient,
    IRulesEngine rulesEngine,
    IStateStore stateStore,
    IWorkspaceManager workspaceManager,
    TaskPlanningService planningService,
    RepositoryWorkflowService repositoryWorkflowService,
    ExecutionJournal executionJournal,
    IOptions<AzureDevOpsOptions> azureDevOpsOptions,
    IOptions<WorkerOptions> workerOptions,
    ILogger<TaskOrchestrator> logger) : ITaskOrchestrator
{
    public async Task<IReadOnlyList<string>> RunQueueAsync(CancellationToken cancellationToken)
    {
        await PrepareRuntimeAsync(cancellationToken);

        var aiWork = await azureDevOpsClient.QueryAiWorkAsync(cancellationToken);
        var aiRework = await azureDevOpsClient.QueryAiReworkAsync(cancellationToken);
        var queue = rulesEngine.OrderQueue(aiWork, aiRework)
            .Take(workerOptions.Value.MaxItemsPerRun)
            .ToArray();

        logger.LogInformation(
            "Queue loaded: {AiWorkCount} AI-Work, {AiReworkCount} AI-Rework, {SelectedCount} selected.",
            aiWork.Count,
            aiRework.Count,
            queue.Length);

        var executions = new List<string>();
        foreach (var item in queue)
        {
            logger.LogInformation(
                "Queue item selected: #{WorkItemId} {WorkItemType} '{Title}' ({Kind}).",
                item.WorkItemId,
                item.WorkItemType,
                item.Title,
                item.Kind);
            executions.Add(await RunTaskAsync(item.WorkItemId, cancellationToken));
        }

        return executions;
    }

    public async Task<string> RunTaskAsync(int workItemId, CancellationToken cancellationToken)
    {
        await PrepareRuntimeAsync(cancellationToken);

        var workItem = await azureDevOpsClient.GetWorkItemAsync(workItemId, cancellationToken);
        var executionId = Guid.NewGuid().ToString("N");
        var record = new TaskExecutionRecord(
            executionId,
            workItem.WorkItemId,
            workItem.Title,
            workItem.Kind,
            TaskPhase.Queued,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        await stateStore.SaveExecutionAsync(record, cancellationToken);
        await executionJournal.AppendEventAsync(
            executionId,
            TaskPhase.Queued,
            $"Created execution for work item #{workItem.WorkItemId}.",
            cancellationToken);
        logger.LogInformation(
            "Execution {ExecutionId} created for #{WorkItemId} '{Title}'.",
            executionId,
            workItem.WorkItemId,
            workItem.Title);

        await RunInternalAsync(executionId, workItem, ResumeCheckpoint.Planning, isExplicitResume: false, cancellationToken);
        return executionId;
    }

    public async Task ResumeTaskAsync(string executionId, CancellationToken cancellationToken)
    {
        await PrepareRuntimeAsync(cancellationToken);

        var execution = await stateStore.GetExecutionAsync(executionId, cancellationToken)
            ?? throw new InvalidOperationException($"Execution '{executionId}' was not found.");

        var workItem = await azureDevOpsClient.GetWorkItemAsync(execution.WorkItemId, cancellationToken);
        await executionJournal.AppendEventAsync(
            executionId,
            execution.Phase,
            "Resuming execution from persisted state.",
            cancellationToken);
        logger.LogInformation(
            "Resuming execution {ExecutionId} for #{WorkItemId} from phase {Phase}.",
            executionId,
            execution.WorkItemId,
            execution.Phase);

        var resumeCheckpoint = await DetermineResumeCheckpointAsync(executionId, execution.Phase, cancellationToken);
        await executionJournal.AppendEventAsync(
            executionId,
            execution.Phase,
            $"Resume checkpoint resolved to {resumeCheckpoint}.",
            cancellationToken);

        await RunInternalAsync(executionId, workItem, resumeCheckpoint, isExplicitResume: true, cancellationToken);
    }

    private async Task PrepareRuntimeAsync(CancellationToken cancellationToken)
    {
        await stateStore.InitializeAsync(cancellationToken);
        await workspaceManager.PrepareAsync(cancellationToken);
    }

    private async Task<ExecutionOutcome> RunInternalAsync(
        string executionId,
        WorkItemDetails workItem,
        ResumeCheckpoint resumeCheckpoint,
        bool isExplicitResume,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!isExplicitResume &&
                !rulesEngine.IsEligible(workItem, azureDevOpsOptions.Value.DefaultAssignee))
            {
                await executionJournal.UpdatePhaseAsync(
                    executionId,
                    TaskPhase.Skipped,
                    "Work item is no longer eligible for automation.",
                    cancellationToken);
                return ExecutionOutcome.Skipped;
            }

            if (rulesEngine.ExceedsAcceptanceCriteriaLimit(workItem, 10, out var lineCount))
            {
                var tags = workItem.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
                tags.Add("AI-TooLarge");
                await azureDevOpsClient.UpdateTagsAsync(workItem.WorkItemId, tags.ToArray(), cancellationToken);
                await azureDevOpsClient.AddCommentAsync(
                    workItem.WorkItemId,
                    $"Skipped automatically because acceptance criteria contained {lineCount} requirement lines.",
                    cancellationToken);
                await executionJournal.UpdatePhaseAsync(
                    executionId,
                    TaskPhase.Skipped,
                    "Acceptance criteria exceeded the configured limit.",
                    cancellationToken);
                return ExecutionOutcome.Skipped;
            }

            var currentSprint = await azureDevOpsClient.GetCurrentSprintPathAsync(cancellationToken) ?? workItem.IterationPath;
            await EnsureWorkItemReadyAsync(workItem, currentSprint, cancellationToken);

            PlanResult plan;
            if (resumeCheckpoint == ResumeCheckpoint.Planning)
            {
                await executionJournal.UpdatePhaseAsync(executionId, TaskPhase.Planning, "Running planning agent.", cancellationToken);
                plan = await planningService.RunPlanAsync(executionId, workItem, cancellationToken);
            }
            else
            {
                plan = await stateStore.GetPlanAsync(executionId, cancellationToken)
                    ?? throw new InvalidOperationException("Cannot resume without a persisted plan.");
                await executionJournal.AppendEventAsync(
                    executionId,
                    TaskPhase.Planning,
                    "Reusing persisted plan for resume.",
                    cancellationToken);
            }

            var repositoryExecutions = await repositoryWorkflowService.PrepareAsync(
                executionId,
                workItem,
                MapScopeToRepos(plan.Scope),
                preserveExistingWorkspace: resumeCheckpoint != ResumeCheckpoint.Planning,
                cancellationToken);

            ImplementationRunResult implementation;
            if (resumeCheckpoint == ResumeCheckpoint.ValidationAndPublishing)
            {
                implementation = await LoadImplementationResultAsync(executionId, cancellationToken);
                await executionJournal.AppendEventAsync(
                    executionId,
                    TaskPhase.Validating,
                    "Skipping planning and coding; resuming from validation/publishing.",
                    cancellationToken);
            }
            else
            {
                implementation = await repositoryWorkflowService.RunImplementationAsync(
                    executionId,
                    workItem,
                    repositoryExecutions,
                    plan,
                    cancellationToken);
            }

            var publishedRepositories = new List<RepositoryPublishResult>();
            foreach (var repositoryExecution in repositoryExecutions)
            {
                var publication = await repositoryWorkflowService.ValidateAndPublishAsync(
                    executionId,
                    workItem,
                    repositoryExecution,
                    cancellationToken);
                await azureDevOpsClient.LinkPullRequestAsync(repositoryExecution.ChildTask.WorkItemId, publication.PullRequest, cancellationToken);
                publishedRepositories.Add(publication);
            }

            await azureDevOpsClient.UpdateTagsAsync(workItem.WorkItemId, rulesEngine.BuildCompletionTags(workItem), cancellationToken);
            await azureDevOpsClient.AddCommentAsync(
                workItem.WorkItemId,
                WorkItemCommentFormatter.BuildSuccessComment(executionId, publishedRepositories, implementation),
                cancellationToken);
            await executionJournal.UpdatePhaseAsync(executionId, TaskPhase.Completed, "Execution completed successfully.", cancellationToken);
            return ExecutionOutcome.Completed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Execution {ExecutionId} failed for work item {WorkItemId}", executionId, workItem.WorkItemId);
            await azureDevOpsClient.UpdateTagsAsync(workItem.WorkItemId, rulesEngine.BuildFailureTags(workItem), cancellationToken);
            await azureDevOpsClient.AddCommentAsync(
                workItem.WorkItemId,
                WorkItemCommentFormatter.BuildFailureComment(executionId, ex),
                cancellationToken);
            await executionJournal.UpdatePhaseAsync(executionId, TaskPhase.Failed, ex.Message, cancellationToken);
            throw;
        }
    }

    private async Task EnsureWorkItemReadyAsync(WorkItemDetails workItem, string currentSprint, CancellationToken cancellationToken)
    {
        if (!string.Equals(workItem.State, "Active", StringComparison.OrdinalIgnoreCase))
        {
            await azureDevOpsClient.UpdateWorkItemStateAsync(workItem.WorkItemId, "Active", cancellationToken);
            logger.LogInformation("Activated work item #{WorkItemId}.", workItem.WorkItemId);
        }

        if (string.IsNullOrWhiteSpace(workItem.AssignedTo))
        {
            await azureDevOpsClient.AssignWorkItemAsync(workItem.WorkItemId, azureDevOpsOptions.Value.DefaultAssignee, cancellationToken);
            logger.LogInformation("Assigned work item #{WorkItemId} to {Assignee}.", workItem.WorkItemId, azureDevOpsOptions.Value.DefaultAssignee);
        }

        if (workItem.Kind == QueueItemKind.AiWork && !string.IsNullOrWhiteSpace(currentSprint))
        {
            await azureDevOpsClient.UpdateIterationPathAsync(workItem.WorkItemId, currentSprint, cancellationToken);
            logger.LogInformation("Set iteration for #{WorkItemId} to '{IterationPath}'.", workItem.WorkItemId, currentSprint);
        }
    }

    private static IReadOnlyList<RepoKind> MapScopeToRepos(WorkItemScope scope) => scope switch
    {
        WorkItemScope.Backend => [RepoKind.Backend],
        WorkItemScope.Frontend => [RepoKind.Frontend],
        _ => [RepoKind.Backend, RepoKind.Frontend],
    };

    private async Task<ResumeCheckpoint> DetermineResumeCheckpointAsync(string executionId, TaskPhase currentPhase, CancellationToken cancellationToken)
    {
        if (currentPhase is TaskPhase.Completed or TaskPhase.Skipped)
        {
            throw new InvalidOperationException($"Execution is in phase {currentPhase} and cannot be resumed.");
        }

        if (currentPhase != TaskPhase.Failed)
        {
            return currentPhase switch
            {
                TaskPhase.Validating or TaskPhase.Publishing => ResumeCheckpoint.ValidationAndPublishing,
                TaskPhase.Coding or TaskPhase.Reviewing or TaskPhase.ApplyingReview => ResumeCheckpoint.Implementation,
                _ => ResumeCheckpoint.Planning,
            };
        }

        var events = await stateStore.GetEventsAsync(executionId, cancellationToken);
        var lastMeaningfulPhase = events
            .Select(item => item.Phase)
            .LastOrDefault(phase => phase != TaskPhase.Failed && phase != TaskPhase.Queued);

        return lastMeaningfulPhase switch
        {
            TaskPhase.Validating or TaskPhase.Publishing => ResumeCheckpoint.ValidationAndPublishing,
            TaskPhase.Coding or TaskPhase.Reviewing or TaskPhase.ApplyingReview => ResumeCheckpoint.Implementation,
            TaskPhase.Planning when await stateStore.GetPlanAsync(executionId, cancellationToken) is not null => ResumeCheckpoint.Implementation,
            _ => ResumeCheckpoint.Planning,
        };
    }

    private async Task<ImplementationRunResult> LoadImplementationResultAsync(string executionId, CancellationToken cancellationToken)
    {
        var review = await stateStore.GetLatestReviewResultAsync(executionId, cancellationToken)
            ?? new ReviewResult("Review result unavailable for resumed validation.", [], string.Empty);
        return new ImplementationRunResult(review, AcceptedFindingCount: null);
    }
}
