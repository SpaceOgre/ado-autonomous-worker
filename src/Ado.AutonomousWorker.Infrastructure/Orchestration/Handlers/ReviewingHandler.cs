using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class ReviewingHandler(
    IAgentRunnerFactory agentRunnerFactory,
    IStateStore stateStore,
    ExecutionJournal executionJournal,
    IOptions<AgentOptions> agentOptions,
    IOptions<StorageOptions> storageOptions,
    IOptions<WorkerOptions> workerOptions,
    ILogger<ReviewingHandler> logger
) : WorkItemHandler
{
    public override TaskPhase Phase => TaskPhase.Reviewing;

    protected override async Task<PhaseTransition> ExecuteAsync(
        PhaseContext.WorkItemContext context,
        CancellationToken cancellationToken
    )
    {
        var workItem = context.WorkItem;
        var repositoryExecutions = context.RepositoryExecutions;
        var scopeLabel = RepositoryWorkflowHelpers.FormatRepoKinds(repositoryExecutions.Select(r => r.RepoKind));
        var agentWorkingDirectory = RepositoryWorkflowHelpers.DetermineAgentWorkingDirectory(
            repositoryExecutions.Select(r => r.Workspace.WorkingDirectory)
        );
        var implementationArtifactRoot = Path.Combine(
            storageOptions.Value.GetExecutionArtifactRoot(workItem.WorkItemId, context.ExecutionId),
            "implementation"
        );
        var commentsBlock = WorkItemPromptContextFormatter.FormatComments(
            workItem.Comments,
            workerOptions.Value.MaxPromptComments,
            workerOptions.Value.MaxPromptCommentCharacters
        );

        await context.Journal.AppendEventAsync(
            context.ExecutionId,
            TaskPhase.Reviewing,
            $"Running review agent for {scopeLabel}.",
            cancellationToken
        );

        logger.LogInformation(
            "Execution {ExecutionId}: running review agent for {ScopeLabel}.",
            context.ExecutionId,
            scopeLabel
        );

        var config = agentOptions.Value.Review;
        var (review, reviewUsage) = await agentRunnerFactory
            .GetRunner(config.Provider)
            .RunReviewAsync(
                config,
                await PromptTemplateLoader.ApplyAsync(
                    config,
                    BuildReviewPrompt(workItem, repositoryExecutions, agentWorkingDirectory, commentsBlock),
                    cancellationToken
                ),
                agentWorkingDirectory,
                Path.Combine(implementationArtifactRoot, "review"),
                AgentOutputSchemas.Review,
                executionJournal.CreateOutputObserver(context.ExecutionId),
                cancellationToken
            );

        await stateStore.SaveReviewResultAsync(context.ExecutionId, review, cancellationToken);
        if (reviewUsage is not null)
            await stateStore.RecordAgentUsageAsync(
                context.ExecutionId,
                TaskPhase.Reviewing,
                AgentRole.Review,
                config.Provider,
                reviewUsage,
                cancellationToken
            );
        context.LatestReview = review;

        var acceptedFindings = review.Findings.Where(ReviewFindingFilter.ShouldAccept).ToArray();
        if (acceptedFindings.Length > 0)
        {
            context.AcceptedFindings = acceptedFindings;
            await context.Journal.AppendEventAsync(
                context.ExecutionId,
                TaskPhase.Reviewing,
                $"{acceptedFindings.Length} findings accepted for remediation.",
                cancellationToken
            );
            return PhaseTransition.To(TaskPhase.ApplyingReview);
        }

        var belowThreshold = review.Findings.Count - acceptedFindings.Length;
        var noAcceptedMessage =
            belowThreshold > 0
                ? $"{belowThreshold} finding(s) below acceptance threshold — proceeding to validation."
                : "No findings — proceeding to validation.";
        await context.Journal.AppendEventAsync(
            context.ExecutionId,
            TaskPhase.Reviewing,
            noAcceptedMessage,
            cancellationToken
        );
        return PhaseTransition.To(TaskPhase.Validating);
    }

    private static string BuildReviewPrompt(
        WorkItemDetails parent,
        IReadOnlyList<RepositoryExecution> repositoryExecutions,
        string agentWorkingDirectory,
        string commentsBlock
    )
    {
        var repositorySummary = RepositoryWorkflowHelpers.BuildRepositorySummary(
            repositoryExecutions,
            agentWorkingDirectory
        );
        var repoPaths = string.Join(
            Environment.NewLine,
            repositoryExecutions.Select(re => $"- {re.Workspace.WorkingDirectory}")
        );

        return $$"""
            You are performing a focused code review of a completed implementation. Your job is to find real problems, not speculate.

            Agent working directory: {{agentWorkingDirectory}}

            <work-item id="{{parent.WorkItemId}}" type="{{RepositoryWorkflowHelpers.FormatWorkItemLabel(parent.WorkItemType)}}" title="{{parent.Title}}">
            <parent-feature>{{parent.ParentFeatureTitle ?? "-"}}</parent-feature>
            <description>
            {{parent.Description ?? "-"}}
            </description>
            <acceptance-criteria>
            {{parent.AcceptanceCriteria ?? "-"}}
            </acceptance-criteria>
            <recent-comments>
            {{commentsBlock}}
            </recent-comments>
            </work-item>

            Repositories:
            {{repositorySummary}}

            ## Step 1: Inspect the diff

            For each repository path listed below, run `git log --oneline -5` to understand the commit history, then run `git diff main..HEAD` (or `git diff master..HEAD` if `main` does not exist) to see exactly what was changed on this branch. Base all findings on evidence visible in the diff or in files the diff touches.

            Repository paths:
            {{repoPaths}}

            ## Step 2: Review using four focused lenses

            For each lens, only emit findings that are clearly present in the diff or in code the diff interacts with. Skip anything speculative.

            **Lens 1 — Bugs**
            Category: `bugs`
            Look for: logic errors, off-by-one errors, null/missing-value handling gaps, incorrect assumptions about data, broken contracts with callers (wrong return value, missing state update), race conditions in async code.

            **Lens 2 — Security**
            Category: `security`
            Look for: unvalidated or unsanitized external input reaching sensitive operations, hard-coded credentials or secrets, missing authorization checks on new code paths, data exposure in logs or error responses, insecure defaults.

            **Lens 3 — Test coverage**
            Category: `test-coverage`
            Look for: new logic paths or branches that have no corresponding test, edge cases (empty input, null, error conditions) that are untested, test assertions that are too weak to catch regressions. Only flag missing tests for non-trivial logic.

            **Lens 4 — Error handling**
            Category: `error-handling`
            Look for: empty or swallowed catch blocks, exceptions replaced with silent defaults (null, false, 0) without logging, unhandled async exceptions, retry logic that exhausts silently, callers not checking for failure results.

            ## Step 3: Severity definitions

            Use exactly one of these four severity labels per finding:
            - `critical` — data loss, security breach, or crash in the normal flow; must be fixed before merging.
            - `high` — causes incorrect behavior or a meaningful reliability gap; should be fixed before merging.
            - `medium` — reduces quality or safety but has a workaround; fix is strongly recommended.
            - `low` — minor improvement only; omit unless the finding is directly related to correctness or safety.

            ## Step 4: Output rules

            - Every finding must include a concrete `suggestedFix`: a specific description of the exact code change needed, not a general recommendation.
            - Set `filePath` to the relative path of the affected file when known.
            - Do not report findings about code that was not changed in this diff.
            - Return JSON matching the supplied schema exactly.

            Requirements:
            - Do not create commits, push branches, open pull requests, link work items, or publish changes to Azure DevOps or Git hosting systems.
            """;
    }
}
