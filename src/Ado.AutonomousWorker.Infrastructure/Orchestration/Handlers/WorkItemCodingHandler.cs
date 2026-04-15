using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class WorkItemCodingHandler(
    IAzureDevOpsClient azureDevOpsClient,
    IWorkspaceManager workspaceManager,
    IAgentRunnerFactory agentRunnerFactory,
    IStateStore stateStore,
    ExecutionJournal executionJournal,
    IOptions<AzureDevOpsOptions> azureDevOpsOptions,
    IOptions<AgentOptions> agentOptions,
    IOptions<RepositoryOptions> repositoryOptions,
    IOptions<StorageOptions> storageOptions,
    IOptions<WorkerOptions> workerOptions,
    ILogger<WorkItemCodingHandler> logger) : WorkItemHandler
{
    public override TaskPhase Phase => TaskPhase.Coding;

    protected override async Task<PhaseTransition> ExecuteAsync(
        PhaseContext.WorkItemContext context, CancellationToken cancellationToken)
    {
        var plan = context.Plan
            ?? throw new InvalidOperationException("Plan must be set before entering Coding phase.");
        var workItem = context.WorkItem;
        var executionArtifactRoot = storageOptions.Value.GetExecutionArtifactRoot(workItem.WorkItemId, context.ExecutionId);

        await context.Journal.AppendEventAsync(
            context.ExecutionId, TaskPhase.Coding, $"Preparing repositories for scope {plan.Scope}.", cancellationToken);

        var slug = RepositoryWorkflowHelpers.ComputeSlug(workItem.Title);
        var repositoryExecutions = new List<RepositoryExecution>();

        foreach (var repoKind in RepositoryWorkflowHelpers.MapScopeToRepos(plan.Scope))
        {
            var childTask = workItem.WorkItemType == WorkItemType.Bug
                ? new ChildTaskRecord(workItem.WorkItemId, repoKind, workItem.Title, workItem.State)
                : workItem.ChildTasks.FirstOrDefault(t => t.RepoKind == repoKind)
                    ?? await CreateChildTaskAsync(workItem, repoKind, cancellationToken);
            var repoProfile = repositoryOptions.Value.GetProfile(repoKind);
            var workspace = await workspaceManager.CreateRepoExecutionAsync(
                workItem.WorkItemId, childTask.WorkItemId, repoKind, slug, context.IsExplicitResume, cancellationToken);
            var artifactDirectory = Path.Combine(executionArtifactRoot, repoKind.ToString().ToLowerInvariant());

            var workspaceMessage = $"Using {repoKind} worktree '{workspace.WorkingDirectory}' on branch '{workspace.BranchName}' for child task #{childTask.WorkItemId}.";
            logger.LogInformation("Execution {ExecutionId}: {Message}", context.ExecutionId, workspaceMessage);
            await executionJournal.AppendEventAsync(context.ExecutionId, TaskPhase.Coding, workspaceMessage, cancellationToken);
            repositoryExecutions.Add(new RepositoryExecution(childTask, repoProfile, workspace, artifactDirectory));
        }

        context.RepositoryExecutions = repositoryExecutions;

        var scopeLabel = RepositoryWorkflowHelpers.FormatRepoKinds(repositoryExecutions.Select(r => r.RepoKind));
        var agentWorkingDirectory = RepositoryWorkflowHelpers.DetermineAgentWorkingDirectory(
            repositoryExecutions.Select(r => r.Workspace.WorkingDirectory));
        var commentsBlock = WorkItemPromptContextFormatter.FormatComments(
            workItem.Comments, workerOptions.Value.MaxPromptComments, workerOptions.Value.MaxPromptCommentCharacters);

        await context.Journal.AppendEventAsync(
            context.ExecutionId, TaskPhase.Coding, $"Running coding agent for {scopeLabel}.", cancellationToken);

        var config = agentOptions.Value.Code;
        var codeStart = await agentRunnerFactory.GetRunner(config.Provider).StartSessionAsync(
            AgentRole.Code,
            config,
            await PromptTemplateLoader.ApplyAsync(
                config,
                BuildCodePrompt(workItem, repositoryExecutions, plan, agentWorkingDirectory, commentsBlock),
                cancellationToken),
            agentWorkingDirectory,
            Path.Combine(executionArtifactRoot, "implementation", "code"),
            outputSchema: null,
            executionJournal.CreateOutputObserver(context.ExecutionId),
            cancellationToken);

        await stateStore.SaveAgentSessionAsync(context.ExecutionId, codeStart.Session, cancellationToken);
        if (codeStart.Result.Usage is not null)
            await stateStore.RecordAgentUsageAsync(context.ExecutionId, TaskPhase.Coding, AgentRole.Code, config.Provider, codeStart.Result.Usage, cancellationToken);
        context.CodeSession = codeStart.Session;

        return PhaseTransition.To(TaskPhase.Reviewing);
    }

    private async Task<ChildTaskRecord> CreateChildTaskAsync(WorkItemDetails parent, RepoKind repoKind, CancellationToken cancellationToken)
    {
        var childTaskId = await azureDevOpsClient.CreateChildTaskAsync(
            parent.WorkItemId,
            $"{repoKind} - {parent.Title}",
            [repoKind.ToString()],
            parent.IterationPath,
            azureDevOpsOptions.Value.DefaultAssignee,
            cancellationToken);

        logger.LogInformation(
            "Created child task #{ChildTaskId} for parent #{ParentWorkItemId} in {RepoKind}.",
            childTaskId, parent.WorkItemId, repoKind);

        return new ChildTaskRecord(childTaskId, repoKind, $"{repoKind} - {parent.Title}", "Active");
    }

    private static string BuildCodePrompt(
        WorkItemDetails parent,
        IReadOnlyList<RepositoryExecution> repositoryExecutions,
        PlanResult plan,
        string agentWorkingDirectory,
        string commentsBlock)
    {
        var repositorySummary = RepositoryWorkflowHelpers.BuildRepositorySummary(repositoryExecutions, agentWorkingDirectory);
        return $$"""
        Implement this Azure DevOps work item across the listed repositories as one coordinated change.

        Parent item: #{{parent.WorkItemId}} {{RepositoryWorkflowHelpers.FormatWorkItemLabel(parent.WorkItemType)}} {{parent.Title}}
        Agent working directory: {{agentWorkingDirectory}}
        Plan summary: {{plan.Summary}}
        Parent feature: {{parent.ParentFeatureTitle ?? "-"}}

        Description:
        {{parent.Description}}

        Acceptance Criteria:
        {{parent.AcceptanceCriteria}}

        Recent comments:
        {{commentsBlock}}

        Repositories:
        {{repositorySummary}}

        Requirements:
        - Treat the listed repositories as one implementation scope.
        - Only modify files under the listed repository paths.
        - Coordinate backend and frontend changes together when more than one repository is listed.
        - Keep each repository on its prepared branch.
        - Update or add tests when relevant.
        - Do not create commits, push branches, open pull requests, link work items, or publish changes to Azure DevOps or Git hosting systems.
        - Leave every listed repository in a reviewable state for validation and PR creation.
        """;
    }
}
