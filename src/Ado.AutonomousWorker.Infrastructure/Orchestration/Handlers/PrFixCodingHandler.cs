using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed class PrFixCodingHandler(
    IWorkspaceManager workspaceManager,
    IAgentRunnerFactory agentRunnerFactory,
    IStateStore stateStore,
    ExecutionJournal executionJournal,
    IOptions<AgentOptions> agentOptions,
    IOptions<RepositoryOptions> repositoryOptions,
    IOptions<StorageOptions> storageOptions,
    ILogger<PrFixCodingHandler> logger
) : PrFixHandler
{
    public override TaskPhase Phase => TaskPhase.Coding;

    protected override async Task<PhaseTransition> ExecuteAsync(
        PhaseContext.PrFixContext context,
        CancellationToken cancellationToken
    )
    {
        var prTarget = context.PrTarget;
        var workItem = context.WorkItem;
        var executionArtifactRoot = storageOptions.Value.GetExecutionArtifactRoot(
            workItem.WorkItemId,
            context.ExecutionId
        );

        var repoKind = repositoryOptions.Value.GetRepoKindFromRepository(prTarget.RepositoryName);
        var repoProfile = repositoryOptions.Value.GetProfile(repoKind);

        var childTask =
            workItem.WorkItemType == WorkItemType.Bug
                ? new ChildTaskRecord(workItem.WorkItemId, repoKind, workItem.Title, workItem.State)
                : workItem.ChildTasks.FirstOrDefault(t => t.RepoKind == repoKind)
                    ?? new ChildTaskRecord(workItem.WorkItemId, repoKind, workItem.Title, workItem.State);

        var workspace = await workspaceManager.CheckoutPrBranchAsync(
            workItem.WorkItemId,
            childTask.WorkItemId,
            repoKind,
            prTarget.BranchName,
            context.IsExplicitResume,
            cancellationToken
        );

        var artifactDirectory = Path.Combine(executionArtifactRoot, repoKind.ToString().ToLowerInvariant());
        var repoExecution = new RepositoryExecution(childTask, repoProfile, workspace, artifactDirectory);
        context.RepositoryExecutions = [repoExecution];

        var workspaceMessage =
            $"Using {repoKind} PR worktree '{workspace.WorkingDirectory}' on branch '{workspace.BranchName}' for PR fix.";
        logger.LogInformation("Execution {ExecutionId}: {Message}", context.ExecutionId, workspaceMessage);
        await executionJournal.AppendEventAsync(
            context.ExecutionId,
            TaskPhase.Coding,
            workspaceMessage,
            cancellationToken
        );

        await context.Journal.AppendEventAsync(
            context.ExecutionId,
            TaskPhase.Coding,
            $"Running PR fix coding agent for {repoKind} (PR #{prTarget.PullRequestId}, {context.PrThreads.Count} thread(s)).",
            cancellationToken
        );

        var config = agentOptions.Value.Code;
        var codeStart = await agentRunnerFactory
            .GetRunner(config.Provider)
            .StartSessionAsync(
                AgentRole.Code,
                config,
                await PromptTemplateLoader.ApplyAsync(
                    config,
                    BuildPrFixPrompt(workItem, repoExecution, prTarget, context.PrThreads),
                    cancellationToken
                ),
                workspace.WorkingDirectory,
                Path.Combine(executionArtifactRoot, "implementation", "code"),
                AgentOutputSchemas.PrFixCommit,
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

        if (codeStart.Result.Success && !string.IsNullOrWhiteSpace(codeStart.Result.StructuredPayload))
        {
            try
            {
                var commitPayload = JsonSerializer.Deserialize<PrFixCommitPayload>(codeStart.Result.StructuredPayload);
                context.CommitMessage = commitPayload?.CommitMessage;
            }
            catch (JsonException ex)
            {
                logger.LogWarning(
                    ex,
                    "Execution {ExecutionId}: failed to parse PrFix commit message.",
                    context.ExecutionId
                );
            }
        }

        return PhaseTransition.To(TaskPhase.Validating);
    }

    private static string BuildPrFixPrompt(
        WorkItemDetails workItem,
        RepositoryExecution repoExecution,
        PrTarget prTarget,
        IReadOnlyList<PrReviewThread> threads
    )
    {
        var repositorySummary = RepositoryWorkflowHelpers.BuildRepositorySummary(
            [repoExecution],
            repoExecution.Workspace.WorkingDirectory
        );
        var formattedThreads = FormatPrThreads(threads);
        return $$"""
            Address the pull request review comments below for work item #{{workItem.WorkItemId}}.

            PR: !{{prTarget.PullRequestId}} on branch `{{prTarget.BranchName}}`
            Repository: {{prTarget.RepositoryName}}
            Agent working directory: {{repoExecution.Workspace.WorkingDirectory}}
            Original work: #{{workItem.WorkItemId}} {{RepositoryWorkflowHelpers.FormatWorkItemLabel(
                workItem.WorkItemType
            )}} "{{workItem.Title}}"

            Active review threads:
            {{formattedThreads}}

            Repositories:
            {{repositorySummary}}

            Requirements:
            - Address all issues raised in the active review threads above.
            - Only modify files under the listed repository paths.
            - Do not create commits, push branches, or open pull requests.
            - Leave the repository in a reviewable state.
            - When done, output JSON: { "commitMessage": "<concise one-line summary of the fixes>" }
            """;
    }

    private sealed record PrFixCommitPayload([property: JsonPropertyName("commitMessage")] string? CommitMessage);

    private static string FormatPrThreads(IReadOnlyList<PrReviewThread> threads)
    {
        if (threads.Count == 0)
            return "(no active review threads)";

        var sb = new StringBuilder();
        foreach (var thread in threads)
        {
            var location = thread.FilePath is not null
                ? thread.LineNumber is not null
                    ? $"{thread.FilePath}:{thread.LineNumber}"
                    : thread.FilePath
                : "(general)";
            sb.AppendLine($"[Thread #{thread.ThreadId} — {location}]");
            foreach (var comment in thread.Comments)
                sb.AppendLine($"{comment.Author}: {comment.Content}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
