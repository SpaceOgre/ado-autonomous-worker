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
    ExecutionJournal executionJournal,
    TaskStateMachine stateMachine,
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
            aiWork.Count, aiRework.Count, queue.Length);

        var executions = new List<string>();
        foreach (var item in queue)
        {
            logger.LogInformation(
                "Queue item selected: #{WorkItemId} {WorkItemType} '{Title}' ({Kind}).",
                item.WorkItemId, item.WorkItemType, item.Title, item.Kind);
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
            executionId, workItem.WorkItemId, workItem.Title);

        var context = new PhaseContext
        {
            ExecutionId = executionId,
            WorkItem = workItem,
            IsExplicitResume = false,
            Journal = executionJournal,
        };

        await RunWithMachineAsync(context, TaskPhase.Queued, cancellationToken);
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
            executionId, execution.WorkItemId, execution.Phase);

        var events = await stateStore.GetEventsAsync(executionId, cancellationToken);
        var startPhase = ResolveResumePhase(execution.Phase, events);

        await executionJournal.AppendEventAsync(
            executionId,
            execution.Phase,
            $"Resume start phase resolved to {startPhase}.",
            cancellationToken);

        var context = new PhaseContext
        {
            ExecutionId = executionId,
            WorkItem = workItem,
            IsExplicitResume = true,
            Journal = executionJournal,
        };

        // Pre-populate context from stateStore based on where we are resuming from.
        if (startPhase > TaskPhase.Planning)
        {
            context.Plan = await stateStore.GetPlanAsync(executionId, cancellationToken)
                ?? throw new InvalidOperationException("Cannot resume without a persisted plan.");
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

    private async Task RunWithMachineAsync(PhaseContext context, TaskPhase startPhase, CancellationToken cancellationToken)
    {
        try
        {
            await stateMachine.RunAsync(context, startPhase, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Execution {ExecutionId} failed for work item {WorkItemId}",
                context.ExecutionId, context.WorkItem.WorkItemId);
            await azureDevOpsClient.UpdateTagsAsync(
                context.WorkItem.WorkItemId,
                rulesEngine.BuildFailureTags(context.WorkItem),
                cancellationToken);
            await azureDevOpsClient.AddCommentAsync(
                context.WorkItem.WorkItemId,
                WorkItemCommentFormatter.BuildFailureComment(context.ExecutionId, ex),
                cancellationToken);
            await executionJournal.UpdatePhaseAsync(
                context.ExecutionId, TaskPhase.Failed, ex.Message, cancellationToken);
            throw;
        }
    }

    private static TaskPhase ResolveResumePhase(TaskPhase recorded, IReadOnlyList<ExecutionEventRecord> events)
    {
        if (recorded is TaskPhase.Completed or TaskPhase.Skipped)
            throw new InvalidOperationException($"Execution is in phase {recorded} and cannot be resumed.");

        if (recorded != TaskPhase.Failed)
            return recorded;

        // Failed: find the last meaningful phase from the event log and resume from there.
        return events
            .Select(e => e.Phase)
            .LastOrDefault(p => p is not TaskPhase.Failed and not TaskPhase.Queued,
                           TaskPhase.Queued);
    }

    private async Task PrepareRuntimeAsync(CancellationToken cancellationToken)
    {
        await stateStore.InitializeAsync(cancellationToken);
        await workspaceManager.PrepareAsync(cancellationToken);
    }
}
