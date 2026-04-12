using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Services;
using Ado.AutonomousWorker.Infrastructure.Workspaces;
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

        await RunInternalAsync(executionId, workItem, isExplicitResume: false, cancellationToken);
        return executionId;
    }

    public async Task ResumeTaskAsync(string executionId, CancellationToken cancellationToken)
    {
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

        await RunInternalAsync(executionId, workItem, isExplicitResume: true, cancellationToken);
    }

    private async Task PrepareRuntimeAsync(CancellationToken cancellationToken)
    {
        await stateStore.InitializeAsync(cancellationToken);
        await workspaceManager.PrepareAsync(cancellationToken);
    }

    private async Task RunInternalAsync(string executionId, WorkItemDetails workItem, bool isExplicitResume, CancellationToken cancellationToken)
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
                return;
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
                return;
            }

            var currentSprint = await azureDevOpsClient.GetCurrentSprintPathAsync(cancellationToken) ?? workItem.IterationPath;
            await EnsureWorkItemReadyAsync(workItem, currentSprint, cancellationToken);

            await executionJournal.UpdatePhaseAsync(executionId, TaskPhase.Planning, "Running planning agent.", cancellationToken);
            var plan = await planningService.RunPlanAsync(executionId, workItem, cancellationToken);

            var repositoryExecutions = await repositoryWorkflowService.PrepareAsync(
                executionId,
                workItem,
                MapScopeToRepos(plan.Scope),
                cancellationToken);

            await repositoryWorkflowService.RunImplementationAsync(
                executionId,
                workItem,
                repositoryExecutions,
                plan,
                cancellationToken);

            foreach (var repositoryExecution in repositoryExecutions)
            {
                var pr = await repositoryWorkflowService.ValidateAndPublishAsync(
                    executionId,
                    workItem,
                    repositoryExecution,
                    cancellationToken);
                await azureDevOpsClient.LinkPullRequestAsync(repositoryExecution.ChildTask.WorkItemId, pr, cancellationToken);
            }

            await azureDevOpsClient.UpdateTagsAsync(workItem.WorkItemId, rulesEngine.BuildCompletionTags(workItem), cancellationToken);
            await azureDevOpsClient.AddCommentAsync(workItem.WorkItemId, $"Execution {executionId} completed successfully.", cancellationToken);
            await executionJournal.UpdatePhaseAsync(executionId, TaskPhase.Completed, "Execution completed successfully.", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Execution {ExecutionId} failed for work item {WorkItemId}", executionId, workItem.WorkItemId);
            await azureDevOpsClient.UpdateTagsAsync(workItem.WorkItemId, rulesEngine.BuildFailureTags(workItem), cancellationToken);
            await azureDevOpsClient.AddCommentAsync(workItem.WorkItemId, $"Execution {executionId} failed: {ex.Message}", cancellationToken);
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
}
