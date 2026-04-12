using System.Text.RegularExpressions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Infrastructure.Agents;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Ado.AutonomousWorker.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal sealed partial class RepositoryWorkflowService(
    IAzureDevOpsClient azureDevOpsClient,
    IWorkspaceManager workspaceManager,
    IAgentRunnerFactory agentRunnerFactory,
    IStateStore stateStore,
    ExecutionJournal executionJournal,
    ProcessCommandRunner commandRunner,
    IOptions<AzureDevOpsOptions> azureDevOpsOptions,
    IOptions<AgentOptions> agentOptions,
    IOptions<RepositoryOptions> repositoryOptions,
    IOptions<StorageOptions> storageOptions,
    ILogger<RepositoryWorkflowService> logger)
{
    public async Task<IReadOnlyList<RepositoryExecution>> PrepareAsync(
        string executionId,
        WorkItemDetails parent,
        IReadOnlyList<RepoKind> repoKinds,
        CancellationToken cancellationToken)
    {
        var slug = SlugRegex().Replace(parent.Title.ToLowerInvariant(), "-").Trim('-');
        var executionArtifactRoot = storageOptions.Value.GetExecutionArtifactRoot(parent.WorkItemId, executionId);
        var repositoryExecutions = new List<RepositoryExecution>();

        foreach (var repoKind in repoKinds)
        {
            var childTask = parent.ChildTasks.FirstOrDefault(task => task.RepoKind == repoKind) ??
                            await CreateChildTaskAsync(parent, repoKind, cancellationToken);
            var repoProfile = GetRepositoryProfile(repoKind);
            logger.LogInformation(
                "Execution {ExecutionId}: preparing {RepoKind} repo for child task #{ChildTaskId}.",
                executionId,
                repoKind,
                childTask.WorkItemId);

            var workspace = await workspaceManager.CreateRepoExecutionAsync(parent.WorkItemId, childTask.WorkItemId, repoKind, slug, cancellationToken);
            var artifactDirectory = Path.Combine(executionArtifactRoot, repoKind.ToString().ToLowerInvariant());
            var workspaceMessage =
                $"Using {repoKind} worktree '{workspace.WorkingDirectory}' on branch '{workspace.BranchName}' for child task #{childTask.WorkItemId}.";
            logger.LogInformation("Execution {ExecutionId}: {WorkspaceMessage}", executionId, workspaceMessage);
            await executionJournal.AppendEventAsync(executionId, TaskPhase.Coding, workspaceMessage, cancellationToken);

            repositoryExecutions.Add(new RepositoryExecution(childTask, repoProfile, workspace, artifactDirectory));
        }

        return repositoryExecutions;
    }

    public async Task RunImplementationAsync(
        string executionId,
        WorkItemDetails parent,
        IReadOnlyList<RepositoryExecution> repositoryExecutions,
        PlanResult plan,
        CancellationToken cancellationToken)
    {
        var scopeLabel = FormatRepoKinds(repositoryExecutions.Select(item => item.RepoKind));
        var agentWorkingDirectory = DetermineAgentWorkingDirectory(repositoryExecutions.Select(item => item.Workspace.WorkingDirectory));
        var implementationArtifactRoot = Path.Combine(
            storageOptions.Value.GetExecutionArtifactRoot(parent.WorkItemId, executionId),
            "implementation");

        await executionJournal.UpdatePhaseAsync(executionId, TaskPhase.Coding, $"Running coding agent for {scopeLabel}.", cancellationToken);
        var codeRunner = agentRunnerFactory.GetRunner(agentOptions.Value.Code.Provider);
        var codeStart = await codeRunner.StartSessionAsync(
            AgentRole.Code,
            agentOptions.Value.Code,
            BuildCodePrompt(parent, repositoryExecutions, plan, agentWorkingDirectory),
            agentWorkingDirectory,
            Path.Combine(implementationArtifactRoot, "code"),
            outputSchema: null,
            executionJournal.CreateOutputObserver(executionId),
            cancellationToken);

        await stateStore.SaveAgentSessionAsync(executionId, codeStart.Session, cancellationToken);

        await executionJournal.UpdatePhaseAsync(executionId, TaskPhase.Reviewing, $"Running review agent for {scopeLabel}.", cancellationToken);
        var reviewRunner = agentRunnerFactory.GetRunner(agentOptions.Value.Review.Provider);
        var review = await reviewRunner.RunReviewAsync(
            agentOptions.Value.Review,
            BuildReviewPrompt(parent, repositoryExecutions, agentWorkingDirectory),
            agentWorkingDirectory,
            Path.Combine(implementationArtifactRoot, "review"),
            AgentOutputSchemas.Review,
            executionJournal.CreateOutputObserver(executionId),
            cancellationToken);

        await stateStore.SaveReviewResultAsync(executionId, review, cancellationToken);
        var acceptedFindings = review.Findings.Where(ShouldAcceptFinding).ToArray();
        if (acceptedFindings.Length > 0)
        {
            await executionJournal.UpdatePhaseAsync(
                executionId,
                TaskPhase.ApplyingReview,
                $"Resuming coding agent for {scopeLabel} with {acceptedFindings.Length} accepted findings.",
                cancellationToken);
            await codeRunner.ResumeSessionAsync(
                codeStart.Session,
                agentOptions.Value.Code,
                BuildReviewResumePrompt(acceptedFindings),
                Path.Combine(implementationArtifactRoot, "resume"),
                executionJournal.CreateOutputObserver(executionId),
                cancellationToken);
            return;
        }

        logger.LogInformation(
            "Execution {ExecutionId}: no accepted review findings for {ScopeLabel}.",
            executionId,
            scopeLabel);
    }

    public async Task<PullRequestInfo> ValidateAndPublishAsync(
        string executionId,
        WorkItemDetails parent,
        RepositoryExecution repositoryExecution,
        CancellationToken cancellationToken)
    {
        var childTask = repositoryExecution.ChildTask;
        var repoKind = repositoryExecution.RepoKind;
        var repoProfile = repositoryExecution.Profile;
        var workspace = repositoryExecution.Workspace;
        var artifactDirectory = repositoryExecution.ArtifactDirectory;

        await executionJournal.UpdatePhaseAsync(executionId, TaskPhase.Validating, $"Running validation for {repoKind}.", cancellationToken);
        foreach (var validationCommand in repoProfile.ValidationCommands)
        {
            logger.LogInformation(
                "Execution {ExecutionId}: running validation for {RepoKind}: {Command}",
                executionId,
                repoKind,
                validationCommand);
            var validationResult = await commandRunner.RunPowerShellAsync(
                validationCommand,
                workspace.WorkingDirectory,
                Path.Combine(artifactDirectory, $"{NormalizeFileName(validationCommand)}.log"),
                cancellationToken);

            if (!validationResult.Success)
            {
                throw new InvalidOperationException($"Validation failed for {repoKind}: {validationResult.StandardError}");
            }

            logger.LogInformation("Execution {ExecutionId}: validation passed for {RepoKind}: {Command}", executionId, repoKind, validationCommand);
        }

        await executionJournal.UpdatePhaseAsync(executionId, TaskPhase.Publishing, $"Creating commit and PR for {repoKind}.", cancellationToken);
        await RunGitAsync(workspace.WorkingDirectory, artifactDirectory, "git-add.log", ["add", "-A"], cancellationToken);
        await RunGitAllowingNoChangesAsync(
            workspace.WorkingDirectory,
            artifactDirectory,
            "git-commit.log",
            ["commit", "-m", $"#{childTask.WorkItemId}: {parent.Title}"],
            cancellationToken);
        await RunGitAsync(workspace.WorkingDirectory, artifactDirectory, "git-push.log", ["push", "-u", "origin", workspace.BranchName], cancellationToken);

        var pullRequest = await azureDevOpsClient.CreateDraftPullRequestAsync(
            repoProfile.AzureDevOpsRepository,
            workspace.BranchName,
            repoProfile.MainBranch,
            $"#{childTask.WorkItemId}: {parent.Title}",
            $"Resolved: #{childTask.WorkItemId}",
            cancellationToken);

        logger.LogInformation(
            "Execution {ExecutionId}: created draft PR #{PullRequestId} for {RepoKind}.",
            executionId,
            pullRequest.PullRequestId,
            repoKind);

        return pullRequest;
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
            childTaskId,
            parent.WorkItemId,
            repoKind);

        return new ChildTaskRecord(childTaskId, repoKind, $"{repoKind} - {parent.Title}", "Active");
    }

    private async Task RunGitAsync(
        string workingDirectory,
        string artifactDirectory,
        string artifactName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            "git",
            ["-C", workingDirectory, .. arguments],
            workingDirectory,
            Path.Combine(artifactDirectory, artifactName),
            environmentVariables: null,
            activityName: null,
            outputCallback: null,
            cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException(result.StandardError);
        }
    }

    private async Task RunGitAllowingNoChangesAsync(
        string workingDirectory,
        string artifactDirectory,
        string artifactName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            "git",
            ["-C", workingDirectory, .. arguments],
            workingDirectory,
            Path.Combine(artifactDirectory, artifactName),
            environmentVariables: null,
            activityName: null,
            outputCallback: null,
            cancellationToken);

        if (!result.Success &&
            !result.StandardOutput.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) &&
            !result.StandardError.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(result.StandardError);
        }
    }

    private RepositoryProfile GetRepositoryProfile(RepoKind repoKind) => repoKind switch
    {
        RepoKind.Backend => repositoryOptions.Value.Backend,
        RepoKind.Frontend => repositoryOptions.Value.Frontend,
        _ => throw new InvalidOperationException($"Unknown repo kind '{repoKind}'."),
    };

    private static bool ShouldAcceptFinding(ReviewFinding finding)
    {
        var severity = finding.Severity.ToLowerInvariant();
        if (severity.Contains("critical") || severity.Contains("high"))
        {
            return true;
        }

        var text = $"{finding.Title} {finding.Rationale}".ToLowerInvariant();
        return text.Contains("bug") || text.Contains("security") || text.Contains("correctness") || text.Contains("missing test");
    }

    private static string BuildCodePrompt(
        WorkItemDetails parent,
        IReadOnlyList<RepositoryExecution> repositoryExecutions,
        PlanResult plan,
        string agentWorkingDirectory)
    {
        var repositorySummary = BuildRepositorySummary(repositoryExecutions, agentWorkingDirectory);
        return $$"""
        Implement this Azure DevOps work item across the listed repositories as one coordinated change.

        Parent item: #{{parent.WorkItemId}} {{FormatWorkItemLabel(parent.WorkItemType)}} {{parent.Title}}
        Agent working directory: {{agentWorkingDirectory}}
        Plan summary: {{plan.Summary}}

        Repositories:
        {{repositorySummary}}

        Requirements:
        - Treat the listed repositories as one implementation scope.
        - Only modify files under the listed repository paths.
        - Coordinate backend and frontend changes together when more than one repository is listed.
        - Keep each repository on its prepared branch.
        - Update or add tests when relevant.
        - Leave every listed repository in a reviewable state for validation and PR creation.
        """;
    }

    private static string BuildReviewPrompt(
        WorkItemDetails parent,
        IReadOnlyList<RepositoryExecution> repositoryExecutions,
        string agentWorkingDirectory)
    {
        var repositorySummary = BuildRepositorySummary(repositoryExecutions, agentWorkingDirectory);
        return $$"""
        Review the coordinated changes across the listed repositories for correctness, regressions, integration mismatches, security issues, and missing tests.
        Return JSON that matches the supplied schema exactly.

        Parent item: #{{parent.WorkItemId}} {{FormatWorkItemLabel(parent.WorkItemType)}} {{parent.Title}}
        Agent working directory: {{agentWorkingDirectory}}

        Repositories:
        {{repositorySummary}}
        """;
    }

    private static string BuildReviewResumePrompt(IReadOnlyList<ReviewFinding> findings)
    {
        var formattedFindings = string.Join(
            Environment.NewLine,
            findings.Select(finding => $"- [{finding.Severity}] {finding.Title}: {finding.Rationale}"));

        return $$"""
        Apply the accepted review findings and leave the repository ready for validation.

        Accepted findings:
        {{formattedFindings}}
        """;
    }

    private static string BuildRepositorySummary(IReadOnlyList<RepositoryExecution> repositoryExecutions, string agentWorkingDirectory)
    {
        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            repositoryExecutions.Select(repositoryExecution =>
            {
                var workspacePath = repositoryExecution.Workspace.WorkingDirectory;
                var relativePath = Path.GetRelativePath(agentWorkingDirectory, workspacePath);
                var validation = repositoryExecution.Profile.ValidationCommands.Count == 0
                    ? "None"
                    : string.Join(" | ", repositoryExecution.Profile.ValidationCommands);

                return $$"""
                [{{repositoryExecution.RepoKind}}]
                Child task: #{{repositoryExecution.ChildTask.WorkItemId}} {{repositoryExecution.ChildTask.Title}}
                Branch: {{repositoryExecution.Workspace.BranchName}}
                Relative path: {{relativePath}}
                Full path: {{workspacePath}}
                Validation: {{validation}}
                """;
            }));
    }

    private static string DetermineAgentWorkingDirectory(IEnumerable<string> workingDirectories)
    {
        var directories = workingDirectories
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (directories.Length == 0)
        {
            throw new InvalidOperationException("At least one working directory is required.");
        }

        if (directories.Length == 1)
        {
            return directories[0];
        }

        var commonPath = directories[0];
        foreach (var directory in directories.Skip(1))
        {
            commonPath = GetCommonPath(commonPath, directory);
            if (string.IsNullOrWhiteSpace(commonPath))
            {
                return Directory.GetCurrentDirectory();
            }
        }

        return commonPath;
    }

    private static string GetCommonPath(string left, string right)
    {
        var leftRoot = Path.GetPathRoot(left) ?? string.Empty;
        var rightRoot = Path.GetPathRoot(right) ?? string.Empty;
        if (!string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var leftSegments = left[leftRoot.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var rightSegments = right[rightRoot.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var sharedSegmentCount = 0;
        while (sharedSegmentCount < leftSegments.Length &&
               sharedSegmentCount < rightSegments.Length &&
               string.Equals(leftSegments[sharedSegmentCount], rightSegments[sharedSegmentCount], StringComparison.OrdinalIgnoreCase))
        {
            sharedSegmentCount++;
        }

        var commonPath = leftRoot;
        for (var i = 0; i < sharedSegmentCount; i++)
        {
            commonPath = Path.Combine(commonPath, leftSegments[i]);
        }

        return commonPath;
    }

    private static string FormatRepoKinds(IEnumerable<RepoKind> repoKinds)
    {
        return string.Join(" + ", repoKinds.Distinct().Select(repoKind => repoKind.ToString()));
    }

    private static string NormalizeFileName(string value)
    {
        return SlugRegex().Replace(value.ToLowerInvariant(), "-").Trim('-');
    }

    private static string FormatWorkItemLabel(WorkItemType workItemType)
    {
        return workItemType switch
        {
            WorkItemType.UserStory => "User Story",
            WorkItemType.Bug => "Bug",
            WorkItemType.Task => "Task",
            _ => "Work Item",
        };
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex SlugRegex();
}
