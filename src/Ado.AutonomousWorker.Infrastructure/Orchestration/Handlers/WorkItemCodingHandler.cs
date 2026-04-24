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
    ILogger<WorkItemCodingHandler> logger
) : WorkItemHandler
{
    public override TaskPhase Phase => TaskPhase.Coding;

    protected override async Task<PhaseTransition> ExecuteAsync(
        PhaseContext.WorkItemContext context,
        CancellationToken cancellationToken
    )
    {
        var plan =
            context.Plan ?? throw new InvalidOperationException("Plan must be set before entering Coding phase.");
        var workItem = context.WorkItem;
        var executionArtifactRoot = storageOptions.Value.GetExecutionArtifactRoot(
            workItem.WorkItemId,
            context.ExecutionId
        );

        await context.Journal.AppendEventAsync(
            context.ExecutionId,
            TaskPhase.Coding,
            $"Preparing repositories for scope {plan.Scope}.",
            cancellationToken
        );

        var slug = RepositoryWorkflowHelpers.ComputeSlug(workItem.Title);
        var repositoryExecutions = new List<RepositoryExecution>();

        foreach (var repoKind in RepositoryWorkflowHelpers.MapScopeToRepos(plan.Scope))
        {
            var childTask =
                workItem.WorkItemType is WorkItemType.Bug or WorkItemType.Task
                    ? new ChildTaskRecord(workItem.WorkItemId, repoKind, workItem.Title, workItem.State)
                    : workItem.ChildTasks.FirstOrDefault(t => t.RepoKind == repoKind)
                        ?? await CreateChildTaskAsync(workItem, context.CurrentSprintPath, repoKind, cancellationToken);
            var repoProfile = repositoryOptions.Value.GetProfile(repoKind);
            var workspace = await workspaceManager.CreateRepoExecutionAsync(
                workItem.WorkItemId,
                childTask.WorkItemId,
                repoKind,
                slug,
                context.IsExplicitResume,
                cancellationToken
            );
            var artifactDirectory = Path.Combine(executionArtifactRoot, repoKind.ToString().ToLowerInvariant());

            var workspaceMessage =
                $"Using {repoKind} worktree '{workspace.WorkingDirectory}' on branch '{workspace.BranchName}' for child task #{childTask.WorkItemId}.";
            logger.LogInformation("Execution {ExecutionId}: {Message}", context.ExecutionId, workspaceMessage);
            await executionJournal.AppendEventAsync(
                context.ExecutionId,
                TaskPhase.Coding,
                workspaceMessage,
                cancellationToken
            );
            repositoryExecutions.Add(new RepositoryExecution(childTask, repoProfile, workspace, artifactDirectory));

            // Link user-story child tasks (not bugs, which reuse the parent work item ID).
            if (childTask.WorkItemId != workItem.WorkItemId)
            {
                await stateStore.LinkChildTaskToExecutionAsync(
                    context.ExecutionId,
                    childTask.WorkItemId,
                    repoKind.ToString(),
                    childTask.Title,
                    cancellationToken
                );
            }
        }

        context.RepositoryExecutions = repositoryExecutions;

        var scopeLabel = RepositoryWorkflowHelpers.FormatRepoKinds(repositoryExecutions.Select(r => r.RepoKind));
        var agentWorkingDirectory = RepositoryWorkflowHelpers.DetermineAgentWorkingDirectory(
            repositoryExecutions.Select(r => r.Workspace.WorkingDirectory)
        );
        var commentsBlock = WorkItemPromptContextFormatter.FormatComments(
            workItem.Comments,
            workerOptions.Value.MaxPromptComments,
            workerOptions.Value.MaxPromptCommentCharacters
        );

        await context.Journal.AppendEventAsync(
            context.ExecutionId,
            TaskPhase.Coding,
            $"Running coding agent for {scopeLabel}.",
            cancellationToken
        );

        logger.LogDebug(
            "Execution {ExecutionId}: coding with {TestIntentCount} test intent item(s).",
            context.ExecutionId,
            plan.TestIntent.Count
        );

        var config = agentOptions.Value.Code;
        var codeStart = await agentRunnerFactory
            .GetRunner(config.Provider)
            .StartSessionAsync(
                AgentRole.Code,
                config,
                await PromptTemplateLoader.ApplyAsync(
                    config,
                    BuildCodePrompt(workItem, repositoryExecutions, plan, agentWorkingDirectory, commentsBlock),
                    cancellationToken
                ),
                agentWorkingDirectory,
                Path.Combine(executionArtifactRoot, "implementation", "code"),
                outputSchema: null,
                executionJournal.CreateOutputObserver(context.ExecutionId),
                cancellationToken
            );

        await stateStore.SaveAgentSessionAsync(context.ExecutionId, codeStart.Session, cancellationToken);
        if (codeStart.Result.Usage is not null)
            await stateStore.RecordAgentUsageAsync(
                context.ExecutionId,
                TaskPhase.Coding,
                AgentRole.Code,
                config.Provider,
                codeStart.Result.Usage,
                cancellationToken
            );
        context.CodeSession = codeStart.Session;

        return PhaseTransition.To(TaskPhase.Reviewing);
    }

    private async Task<ChildTaskRecord> CreateChildTaskAsync(
        WorkItemDetails parent,
        string? currentSprintPath,
        RepoKind repoKind,
        CancellationToken cancellationToken
    )
    {
        var childTaskId = await azureDevOpsClient.CreateChildTaskAsync(
            parent.WorkItemId,
            $"{repoKind} - {parent.Title}",
            [repoKind.ToString()],
            currentSprintPath ?? parent.IterationPath,
            azureDevOpsOptions.Value.DefaultAssignee,
            cancellationToken
        );

        logger.LogInformation(
            "Created child task #{ChildTaskId} for parent #{ParentWorkItemId} in {RepoKind}.",
            childTaskId,
            parent.WorkItemId,
            repoKind
        );

        return new ChildTaskRecord(childTaskId, repoKind, $"{repoKind} - {parent.Title}", "Active");
    }

    private static string BuildCodePrompt(
        WorkItemDetails parent,
        IReadOnlyList<RepositoryExecution> repositoryExecutions,
        PlanResult plan,
        string agentWorkingDirectory,
        string commentsBlock
    )
    {
        var repositorySummary = RepositoryWorkflowHelpers.BuildRepositorySummary(
            repositoryExecutions,
            agentWorkingDirectory
        );
        var testIntentBlock =
            plan.TestIntent.Count > 0
                ? $"Add or update tests for the following:\n{string.Join("\n", plan.TestIntent.Select(t => $"- {t}"))}"
                : "Update or add tests where the change introduces non-trivial logic.";

        var hasFrontend = repositoryExecutions.Any(r => r.RepoKind == RepoKind.Frontend);
        if (hasFrontend)
            testIntentBlock += "\nDo not write any tests for the frontend repository.";

        var risksBlock =
            plan.Risks.Count > 0
                ? $"\nRisks to watch for:\n{string.Join("\n", plan.Risks.Select(r => $"- {r}"))}"
                : string.Empty;

        return $$"""
            Implement this Azure DevOps work item across the listed repositories as one coordinated change.

            Agent working directory: {{agentWorkingDirectory}}
            Plan summary: {{plan.Summary}}{{risksBlock}}

            <work-item id="{{parent.WorkItemId}}" type="{{RepositoryWorkflowHelpers.FormatWorkItemLabel(
                parent.WorkItemType
            )}}" title="{{parent.Title}}">
            <parent-feature>{{parent.ParentFeatureTitle ?? "-"}}</parent-feature>
            <description>
            {{parent.Description}}
            </description>
            <acceptance-criteria>
            {{parent.AcceptanceCriteria}}
            </acceptance-criteria>
            <recent-comments>
            {{commentsBlock}}
            </recent-comments>
            </work-item>

            Repositories:
            {{repositorySummary}}

            ## Step 1: Explore before implementing

            Before writing any code, read the existing code in the areas you will modify. Understand:
            - File structure and naming conventions in use.
            - How similar features are already implemented.
            - What test patterns and helper utilities exist.

            ## Step 2: Implement

            Make all changes needed to satisfy the acceptance criteria. Follow the conventions you observed. When more than one repository is in scope, coordinate backend and frontend changes as a single unit — changes that belong together must be consistent across both.

            Only modify files under the listed repository paths. Keep each repository on its prepared branch.

            ## Step 3: Tests

            {{testIntentBlock}}

            ## Step 4: Validate

            After implementing, run the validation commands listed for each repository above to confirm the change compiles and tests pass. Fix any failures before finishing.

            Requirements:
            - Treat the listed repositories as one implementation scope.
            - Only modify files under the listed repository paths.
            - Coordinate backend and frontend changes together when more than one repository is listed.
            - Keep each repository on its prepared branch.
            - Do not create commits, push branches, open pull requests, link work items, or publish changes to Azure DevOps or Git hosting systems.
            - Leave every listed repository in a reviewable state for validation and PR creation.
            """;
    }
}
