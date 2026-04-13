using System.Text.RegularExpressions;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed partial class CodingHandler(
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
    ILogger<CodingHandler> logger) : IPhaseHandler
{
    public TaskPhase Phase => TaskPhase.Coding;

    public async Task<PhaseTransition> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken)
    {
        var plan = context.Plan
            ?? throw new InvalidOperationException("Plan must be set before entering Coding phase.");
        var workItem = context.WorkItem;
        var executionArtifactRoot = storageOptions.Value.GetExecutionArtifactRoot(workItem.WorkItemId, context.ExecutionId);

        // --- Prepare worktrees ---
        await context.Journal.AppendEventAsync(
            context.ExecutionId, TaskPhase.Coding, $"Preparing repositories for scope {plan.Scope}.", cancellationToken);

        var slug = SlugRegex().Replace(workItem.Title.ToLowerInvariant(), "-").Trim('-');
        var repositoryExecutions = new List<RepositoryExecution>();

        foreach (var repoKind in MapScopeToRepos(plan.Scope))
        {
            var childTask = workItem.ChildTasks.FirstOrDefault(t => t.RepoKind == repoKind)
                ?? await CreateChildTaskAsync(workItem, repoKind, cancellationToken);
            var repoProfile = GetRepositoryProfile(repoKind);
            var workspace = await workspaceManager.CreateRepoExecutionAsync(
                workItem.WorkItemId, childTask.WorkItemId, repoKind, slug, context.IsExplicitResume, cancellationToken);
            var artifactDirectory = Path.Combine(executionArtifactRoot, repoKind.ToString().ToLowerInvariant());

            var workspaceMessage = $"Using {repoKind} worktree '{workspace.WorkingDirectory}' on branch '{workspace.BranchName}' for child task #{childTask.WorkItemId}.";
            logger.LogInformation("Execution {ExecutionId}: {Message}", context.ExecutionId, workspaceMessage);
            await executionJournal.AppendEventAsync(context.ExecutionId, TaskPhase.Coding, workspaceMessage, cancellationToken);
            repositoryExecutions.Add(new RepositoryExecution(childTask, repoProfile, workspace, artifactDirectory));
        }

        context.RepositoryExecutions = repositoryExecutions;

        // --- Run coding agent ---
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

    private RepositoryProfile GetRepositoryProfile(RepoKind repoKind) => repoKind switch
    {
        RepoKind.Backend => repositoryOptions.Value.Backend,
        RepoKind.Frontend => repositoryOptions.Value.Frontend,
        _ => throw new InvalidOperationException($"Unknown repo kind '{repoKind}'."),
    };

    private static IReadOnlyList<RepoKind> MapScopeToRepos(WorkItemScope scope) => scope switch
    {
        WorkItemScope.Backend => [RepoKind.Backend],
        WorkItemScope.Frontend => [RepoKind.Frontend],
        _ => [RepoKind.Backend, RepoKind.Frontend],
    };

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

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex SlugRegex();
}
