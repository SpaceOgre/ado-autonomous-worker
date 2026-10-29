using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal sealed class TaskOrchestrator(
    IAzureDevOpsClient azureDevOpsClient,
    IRulesEngine rulesEngine,
    IStateStore stateStore,
    IWorkspaceManager workspaceManager,
    ExecutionJournal executionJournal,
    TaskStateMachine stateMachine,
    IOptions<RepositoryOptions> repositoryOptions,
    IOptions<StorageOptions> storageOptions,
    ILogger<TaskOrchestrator> logger
) : ITaskOrchestrator
{
    public Task<string> RunTaskAsync(QueueItem item, string? iterationOverride, CancellationToken cancellationToken)
    {
        var prTarget = item.PullRequestId is not null
            ? new PrTarget(item.PullRequestId.Value, item.PrRepositoryName!, item.PrBranchName!)
            : null;
        return RunTaskAsync(item.WorkItemId, iterationOverride, prTarget, cancellationToken);
    }

    public Task<string> RunTaskAsync(int workItemId, string? iterationOverride, CancellationToken cancellationToken) =>
        RunTaskAsync(workItemId, iterationOverride, prTarget: null, cancellationToken);

    public async Task<string> RunPrFixAsync(int pullRequestId, CancellationToken cancellationToken)
    {
        var item =
            await azureDevOpsClient.GetPrFixItemAsync(pullRequestId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"PR !{pullRequestId} does not have the required AI-Rework label or has no linked work items."
            );
        return await RunTaskAsync(item, iterationOverride: null, cancellationToken);
    }

    private async Task<string> RunTaskAsync(
        int workItemId,
        string? iterationOverride,
        PrTarget? prTarget,
        CancellationToken cancellationToken
    )
    {
        await PrepareRuntimeAsync(cancellationToken);

        var workItem = await azureDevOpsClient.GetWorkItemAsync(workItemId, cancellationToken);
        var kind = prTarget is not null ? QueueItemKind.AiPrFix : workItem.Kind;
        var executionId = Guid.NewGuid().ToString("N");
        var record = new TaskExecutionRecord(
            executionId,
            workItem.WorkItemId,
            workItem.Title,
            kind,
            TaskPhase.Queued,
            ExecutionStatus.Running,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow
        );

        await stateStore.SaveExecutionAsync(record, cancellationToken);
        if (prTarget is not null)
            await stateStore.LinkPullRequestToExecutionAsync(
                executionId,
                prTarget.PullRequestId,
                prTarget.RepositoryName,
                cancellationToken
            );
        await executionJournal.AppendEventAsync(
            executionId,
            TaskPhase.Queued,
            $"Created execution for work item #{workItem.WorkItemId}.",
            cancellationToken
        );
        logger.LogInformation(
            "Execution {ExecutionId} created for #{WorkItemId} '{Title}' ({Kind}).",
            executionId,
            workItem.WorkItemId,
            workItem.Title,
            kind
        );

        PhaseContext context = prTarget is not null
            ? new PhaseContext.PrFixContext
            {
                ExecutionId = executionId,
                WorkItem = workItem,
                IsExplicitResume = false,
                Journal = executionJournal,
                PrTarget = prTarget,
            }
            : new PhaseContext.WorkItemContext
            {
                ExecutionId = executionId,
                WorkItem = workItem,
                IsExplicitResume = false,
                Journal = executionJournal,
                IterationOverride = iterationOverride,
            };

        await RunWithMachineAsync(context, TaskPhase.Queued, cancellationToken);
        return executionId;
    }

    public async Task ResumeTaskAsync(string executionId, CancellationToken cancellationToken)
    {
        await PrepareRuntimeAsync(cancellationToken);

        var execution =
            await stateStore.GetExecutionAsync(executionId, cancellationToken)
            ?? throw new InvalidOperationException($"Execution '{executionId}' was not found.");

        var workItem = await azureDevOpsClient.GetWorkItemAsync(execution.WorkItemId, cancellationToken);
        var startPhase = ResolveResumePhase(execution);

        await stateStore.UpdateStatusAsync(executionId, ExecutionStatus.Running, cancellationToken);
        await executionJournal.AppendEventAsync(
            executionId,
            execution.Phase,
            $"Resuming execution from phase {startPhase}.",
            cancellationToken
        );
        logger.LogInformation(
            "Resuming execution {ExecutionId} for #{WorkItemId} at phase {Phase}.",
            executionId,
            execution.WorkItemId,
            startPhase
        );

        PhaseContext context;

        var linkedPr = execution.PullRequests?.FirstOrDefault();
        if (execution.Kind == QueueItemKind.AiPrFix && linkedPr is not null)
        {
            var location = await azureDevOpsClient.GetPullRequestLocationAsync(
                linkedPr.PullRequestId,
                cancellationToken
            );
            var prTarget = location is not null
                ? new PrTarget(linkedPr.PullRequestId, location.Value.RepositoryName, location.Value.BranchName)
                : throw new InvalidOperationException(
                    $"Cannot resume AiPrFix execution '{executionId}': PR !{linkedPr.PullRequestId} was not found."
                );

            var prFixContext = new PhaseContext.PrFixContext
            {
                ExecutionId = executionId,
                WorkItem = workItem,
                IsExplicitResume = true,
                Journal = executionJournal,
                PrTarget = prTarget,
            };

            // Coding populates RepositoryExecutions in-memory. When resuming past Coding,
            // that phase is skipped, so we must rebuild it here.
            if (startPhase >= TaskPhase.Validating)
            {
                prFixContext.RepositoryExecutions = await RebuildPrFixRepositoryExecutionsAsync(
                    workItem,
                    prTarget,
                    executionId,
                    cancellationToken
                );
            }

            context = prFixContext;
        }
        else
        {
            var wiContext = new PhaseContext.WorkItemContext
            {
                ExecutionId = executionId,
                WorkItem = workItem,
                IsExplicitResume = true,
                Journal = executionJournal,
            };

            if (startPhase > TaskPhase.Planning)
            {
                wiContext.Plan =
                    await stateStore.GetPlanAsync(executionId, cancellationToken)
                    ?? throw new InvalidOperationException("Cannot resume without a persisted plan.");
            }

            if (startPhase > TaskPhase.Coding && wiContext.Plan is not null)
            {
                wiContext.RepositoryExecutions = await RebuildRepositoryExecutionsAsync(
                    workItem,
                    wiContext.Plan,
                    executionId,
                    cancellationToken
                );
            }

            context = wiContext;
        }

        if (startPhase >= TaskPhase.Reviewing)
        {
            var sessions = await stateStore.GetAgentSessionsAsync(executionId, cancellationToken);
            context.CodeSession = sessions.LastOrDefault(s => s.Role == AgentRole.Code);
        }

        if (startPhase >= TaskPhase.ApplyingReview)
        {
            context.LatestReview = await stateStore.GetLatestReviewResultAsync(executionId, cancellationToken);
        }

        await RunWithMachineAsync(context, startPhase, cancellationToken);
    }

    private async Task RunWithMachineAsync(
        PhaseContext context,
        TaskPhase startPhase,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await stateMachine.RunAsync(context, startPhase, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Status is already set to Failed by the machine's internal catch.
            // Handle ADO side effects here.
            logger.LogError(
                ex,
                "Execution {ExecutionId} failed for work item {WorkItemId}",
                context.ExecutionId,
                context.WorkItem.WorkItemId
            );

            // Write the full stack trace to a file so it can be inspected after the fact,
            // since the terminal UI may obscure console log output.
            try
            {
                var exceptionLogPath = Path.Combine(
                    storageOptions.Value.GetExecutionArtifactRoot(context.WorkItem.WorkItemId, context.ExecutionId),
                    "exception.log"
                );
                Directory.CreateDirectory(Path.GetDirectoryName(exceptionLogPath)!);
                await File.WriteAllTextAsync(exceptionLogPath, ex.ToString(), CancellationToken.None);
            }
            catch
            { /* best-effort */
            }

            // For AiPrFix failures, do not modify work item tags — leave the "AI-Rework" PR label
            // intact so the developer knows the fix still needs attention.
            if (context is not PhaseContext.PrFixContext)
            {
                await azureDevOpsClient.UpdateTagsAsync(
                    context.WorkItem.WorkItemId,
                    rulesEngine.BuildFailureTags(context.WorkItem),
                    cancellationToken
                );
            }

            await azureDevOpsClient.AddCommentAsync(
                context.WorkItem.WorkItemId,
                WorkItemCommentFormatter.BuildFailureComment(context.ExecutionId, ex),
                cancellationToken
            );
            throw;
        }
    }

    private async Task<IReadOnlyList<RepositoryExecution>> RebuildRepositoryExecutionsAsync(
        WorkItemDetails workItem,
        PlanResult plan,
        string executionId,
        CancellationToken cancellationToken
    )
    {
        var slug = RepositoryWorkflowHelpers.ComputeSlug(workItem.Title);
        var executionArtifactRoot = storageOptions.Value.GetExecutionArtifactRoot(workItem.WorkItemId, executionId);

        var childTasks = RepositoryWorkflowHelpers
            .MapScopeToRepos(plan.Scope)
            .Select(repoKind =>
                workItem.WorkItemType is WorkItemType.Bug or WorkItemType.Task
                    ? new ChildTaskRecord(workItem.WorkItemId, repoKind, workItem.Title, workItem.State)
                    : workItem.ChildTasks.FirstOrDefault(t => t.RepoKind == repoKind)
                        ?? throw new InvalidOperationException(
                            $"Cannot resume: no child task found for {repoKind} on work item #{workItem.WorkItemId}."
                        )
            )
            .ToArray();

        var workspace = await workspaceManager.CreateWorkspaceAsync(
            workItem.WorkItemId,
            RepositoryWorkflowHelpers.GetBranchOwner(childTasks).WorkItemId,
            slug,
            preserveExistingWorkspace: true,
            cancellationToken
        );

        var result = childTasks
            .Select(childTask => new RepositoryExecution(
                childTask,
                repositoryOptions.Value.GetProfile(childTask.RepoKind),
                workspace,
                Path.Combine(executionArtifactRoot, childTask.RepoKind.ToString().ToLowerInvariant())
            ))
            .ToArray();

        logger.LogInformation(
            "Execution {ExecutionId}: rebuilt {Count} repository execution(s) for resume.",
            executionId,
            result.Length
        );

        return result;
    }

    private async Task<IReadOnlyList<RepositoryExecution>> RebuildPrFixRepositoryExecutionsAsync(
        WorkItemDetails workItem,
        PrTarget prTarget,
        string executionId,
        CancellationToken cancellationToken
    )
    {
        var workspace = await workspaceManager.CheckoutPrBranchAsync(
            workItem.WorkItemId,
            workItem.WorkItemId,
            prTarget.BranchName,
            preserveExistingWorkspace: true,
            cancellationToken
        );

        logger.LogInformation(
            "Execution {ExecutionId}: rebuilt PR fix repository execution for resume at worktree '{WorktreePath}'.",
            executionId,
            workspace.WorkingDirectory
        );

        return await RepositoryWorkflowHelpers.BuildPrFixRepositoryExecutionsAsync(
            workspaceManager,
            repositoryOptions.Value,
            workItem,
            workspace,
            storageOptions.Value.GetExecutionArtifactRoot(workItem.WorkItemId, executionId),
            cancellationToken
        );
    }

    private static TaskPhase ResolveResumePhase(TaskExecutionRecord execution)
    {
        if (execution.Status is ExecutionStatus.Completed or ExecutionStatus.Skipped)
            throw new InvalidOperationException($"Cannot resume a {execution.Status} execution.");

        // ValidationFailures live in-memory and aren't restored on resume. Re-running Validating
        // regenerates them from the worktree state — same failures if nothing changed, none if
        // the prior fix succeeded — so resume from Validating instead of FixingValidation.
        if (execution.Phase is TaskPhase.FixingValidation)
            return TaskPhase.Validating;

        // Phase always holds the last active workflow phase — correct for both Running and Failed.
        return execution.Phase;
    }

    private async Task PrepareRuntimeAsync(CancellationToken cancellationToken)
    {
        await stateStore.InitializeAsync(cancellationToken);
        await workspaceManager.PrepareAsync(cancellationToken);
    }
}
