using System.Text.Json;
using System.Text.RegularExpressions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Services;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Services;

public sealed partial class TaskOrchestrator(
    IAzureDevOpsClient azureDevOpsClient,
    IRulesEngine rulesEngine,
    IStateStore stateStore,
    IWorkspaceManager workspaceManager,
    IAgentRunnerFactory agentRunnerFactory,
    ProcessCommandRunner commandRunner,
    IOptions<AzureDevOpsOptions> azureDevOpsOptions,
    IOptions<AgentOptions> agentOptions,
    IOptions<RepositoryOptions> repositoryOptions,
    IOptions<StorageOptions> storageOptions,
    IOptions<WorkerOptions> workerOptions,
    ILogger<TaskOrchestrator> logger) : ITaskOrchestrator
{
    public async Task<IReadOnlyList<string>> RunQueueAsync(CancellationToken cancellationToken)
    {
        await stateStore.InitializeAsync(cancellationToken);
        await workspaceManager.PrepareAsync(cancellationToken);

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
        await stateStore.InitializeAsync(cancellationToken);
        await workspaceManager.PrepareAsync(cancellationToken);

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
        await AppendEventAsync(executionId, TaskPhase.Queued, $"Created execution for work item #{workItem.WorkItemId}.", cancellationToken);
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
        await AppendEventAsync(executionId, execution.Phase, "Resuming execution from persisted state.", cancellationToken);
        logger.LogInformation(
            "Resuming execution {ExecutionId} for #{WorkItemId} from phase {Phase}.",
            executionId,
            execution.WorkItemId,
            execution.Phase);
        await RunInternalAsync(executionId, workItem, isExplicitResume: true, cancellationToken);
    }

    private async Task RunInternalAsync(string executionId, WorkItemDetails workItem, bool isExplicitResume, CancellationToken cancellationToken)
    {
        try
        {
            if (!isExplicitResume &&
                !rulesEngine.IsEligible(workItem, azureDevOpsOptions.Value.DefaultAssignee))
            {
                await UpdatePhaseAsync(executionId, TaskPhase.Skipped, "Work item is no longer eligible for automation.", cancellationToken);
                return;
            }

            if (rulesEngine.ExceedsAcceptanceCriteriaLimit(workItem, 10, out var lineCount))
            {
                var tags = workItem.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
                tags.Add("AI-TooLarge");
                await azureDevOpsClient.UpdateTagsAsync(workItem.WorkItemId, tags.ToArray(), cancellationToken);
                await azureDevOpsClient.AddCommentAsync(workItem.WorkItemId, $"Skipped automatically because acceptance criteria contained {lineCount} requirement lines.", cancellationToken);
                await UpdatePhaseAsync(executionId, TaskPhase.Skipped, "Acceptance criteria exceeded the configured limit.", cancellationToken);
                return;
            }

            var currentSprint = await azureDevOpsClient.GetCurrentSprintPathAsync(cancellationToken) ?? workItem.IterationPath;
            await EnsureWorkItemReadyAsync(workItem, currentSprint, cancellationToken);

            await UpdatePhaseAsync(executionId, TaskPhase.Planning, "Running planning agent.", cancellationToken);
            var plan = await RunPlanAsync(executionId, workItem, cancellationToken);

            var repositoryExecutions = await PrepareRepositoryExecutionsAsync(
                executionId,
                workItem,
                MapScopeToRepos(plan.Scope),
                cancellationToken);

            await RunImplementationAsync(executionId, workItem, repositoryExecutions, plan, cancellationToken);

            foreach (var repositoryExecution in repositoryExecutions)
            {
                var pr = await ValidateAndPublishRepositoryAsync(executionId, workItem, repositoryExecution, cancellationToken);
                await azureDevOpsClient.LinkPullRequestAsync(repositoryExecution.ChildTask.WorkItemId, pr, cancellationToken);
            }

            await azureDevOpsClient.UpdateTagsAsync(workItem.WorkItemId, rulesEngine.BuildCompletionTags(workItem), cancellationToken);
            await azureDevOpsClient.AddCommentAsync(workItem.WorkItemId, $"Execution {executionId} completed successfully.", cancellationToken);
            await UpdatePhaseAsync(executionId, TaskPhase.Completed, "Execution completed successfully.", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Execution {ExecutionId} failed for work item {WorkItemId}", executionId, workItem.WorkItemId);
            await azureDevOpsClient.UpdateTagsAsync(workItem.WorkItemId, rulesEngine.BuildFailureTags(workItem), cancellationToken);
            await azureDevOpsClient.AddCommentAsync(workItem.WorkItemId, $"Execution {executionId} failed: {ex.Message}", cancellationToken);
            await UpdatePhaseAsync(executionId, TaskPhase.Failed, ex.Message, cancellationToken);
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

    private async Task<PlanResult> RunPlanAsync(string executionId, WorkItemDetails workItem, CancellationToken cancellationToken)
    {
        var config = agentOptions.Value.Plan;
        var runner = agentRunnerFactory.GetRunner(config.Provider);
        var artifactDirectory = Path.Combine(storageOptions.Value.GetExecutionArtifactRoot(workItem.WorkItemId, executionId), "plan");

        var prompt = $$"""
        Produce a concise implementation plan for this Azure DevOps work item and return JSON matching the provided schema exactly.

        Work item: #{{workItem.WorkItemId}} {{FormatWorkItemLabel(workItem.WorkItemType)}} {{workItem.Title}}
        State: {{workItem.State}}
        Assigned to: {{workItem.AssignedTo}}
        Iteration: {{workItem.IterationPath}}
        Tags: {{string.Join(", ", workItem.Tags)}}

        Description:
        {{workItem.Description}}

        Acceptance Criteria:
        {{workItem.AcceptanceCriteria}}
        """;

        var startResult = await runner.StartSessionAsync(
            AgentRole.Plan,
            config,
            prompt,
            Directory.GetCurrentDirectory(),
            artifactDirectory,
            AgentOutputSchemas.Plan,
            CreateOutputObserver(executionId),
            cancellationToken);

        await stateStore.SaveAgentSessionAsync(executionId, startResult.Session, cancellationToken);
        var plan = ParsePlan(startResult.Result.StructuredPayload) ?? BuildFallbackPlan(workItem);
        await stateStore.SavePlanAsync(executionId, plan, cancellationToken);
        await AppendEventAsync(executionId, TaskPhase.Planning, plan.Summary, cancellationToken);
        logger.LogInformation(
            "Execution {ExecutionId} plan complete: scope {Scope}, repos {Repositories}.",
            executionId,
            plan.Scope,
            string.Join(", ", plan.Repositories));
        return plan;
    }

    private async Task<IReadOnlyList<RepositoryExecution>> PrepareRepositoryExecutionsAsync(
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
            await AppendEventAsync(executionId, TaskPhase.Coding, workspaceMessage, cancellationToken);
            await AppendRunLogAsync(executionId, TaskPhase.Coding, workspaceMessage, cancellationToken);

            repositoryExecutions.Add(new RepositoryExecution(childTask, repoProfile, workspace, artifactDirectory));
        }

        return repositoryExecutions;
    }

    private async Task RunImplementationAsync(
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

        await UpdatePhaseAsync(executionId, TaskPhase.Coding, $"Running coding agent for {scopeLabel}.", cancellationToken);
        var codeRunner = agentRunnerFactory.GetRunner(agentOptions.Value.Code.Provider);
        var codeStart = await codeRunner.StartSessionAsync(
            AgentRole.Code,
            agentOptions.Value.Code,
            BuildCodePrompt(parent, repositoryExecutions, plan, agentWorkingDirectory),
            agentWorkingDirectory,
            Path.Combine(implementationArtifactRoot, "code"),
            outputSchema: null,
            CreateOutputObserver(executionId),
            cancellationToken);

        await stateStore.SaveAgentSessionAsync(executionId, codeStart.Session, cancellationToken);

        await UpdatePhaseAsync(executionId, TaskPhase.Reviewing, $"Running review agent for {scopeLabel}.", cancellationToken);
        var reviewRunner = agentRunnerFactory.GetRunner(agentOptions.Value.Review.Provider);
        var review = await reviewRunner.RunReviewAsync(
            agentOptions.Value.Review,
            BuildReviewPrompt(parent, repositoryExecutions, agentWorkingDirectory),
            agentWorkingDirectory,
            Path.Combine(implementationArtifactRoot, "review"),
            AgentOutputSchemas.Review,
            CreateOutputObserver(executionId),
            cancellationToken);

        await stateStore.SaveReviewResultAsync(executionId, review, cancellationToken);
        var acceptedFindings = review.Findings.Where(ShouldAcceptFinding).ToArray();
        if (acceptedFindings.Length > 0)
        {
            await UpdatePhaseAsync(
                executionId,
                TaskPhase.ApplyingReview,
                $"Resuming coding agent for {scopeLabel} with {acceptedFindings.Length} accepted findings.",
                cancellationToken);
            await codeRunner.ResumeSessionAsync(
                codeStart.Session,
                agentOptions.Value.Code,
                BuildReviewResumePrompt(acceptedFindings),
                Path.Combine(implementationArtifactRoot, "resume"),
                CreateOutputObserver(executionId),
                cancellationToken);
            return;
        }

        logger.LogInformation(
            "Execution {ExecutionId}: no accepted review findings for {ScopeLabel}.",
            executionId,
            scopeLabel);
    }

    private async Task<PullRequestInfo> ValidateAndPublishRepositoryAsync(
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

        await UpdatePhaseAsync(executionId, TaskPhase.Validating, $"Running validation for {repoKind}.", cancellationToken);
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

        await UpdatePhaseAsync(executionId, TaskPhase.Publishing, $"Creating commit and PR for {repoKind}.", cancellationToken);
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

    private async Task RunGitAsync(string workingDirectory, string artifactDirectory, string artifactName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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

    private async Task RunGitAllowingNoChangesAsync(string workingDirectory, string artifactDirectory, string artifactName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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

    private async Task UpdatePhaseAsync(string executionId, TaskPhase phase, string message, CancellationToken cancellationToken)
    {
        await stateStore.UpdatePhaseAsync(executionId, phase, cancellationToken);
        await AppendEventAsync(executionId, phase, message, cancellationToken);
        await AppendRunLogAsync(executionId, phase, message, cancellationToken);
        logger.LogInformation("Execution {ExecutionId} [{Phase}] {Message}", executionId, phase, message);
    }

    private async Task AppendEventAsync(string executionId, TaskPhase phase, string message, CancellationToken cancellationToken)
    {
        await stateStore.AppendEventAsync(new ExecutionEventRecord(executionId, DateTimeOffset.UtcNow, phase, message), cancellationToken);
    }

    private async Task AppendRunLogAsync(string executionId, TaskPhase phase, string message, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(storageOptions.Value.LogRoot);
        var path = Path.Combine(storageOptions.Value.LogRoot, $"{executionId}.log.md");
        await File.AppendAllTextAsync(path, $"- {DateTimeOffset.UtcNow:O} [{phase}] {message}{Environment.NewLine}", cancellationToken);
    }

    private Func<AgentOutputChunk, CancellationToken, Task> CreateOutputObserver(string executionId)
    {
        return (chunk, cancellationToken) => stateStore.AppendAgentOutputChunkAsync(executionId, chunk, cancellationToken);
    }

    private static PlanResult? ParsePlan(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var scope = Enum.Parse<WorkItemScope>(document.RootElement.GetProperty("scope").GetString() ?? "Fullstack", true);
            return new PlanResult(
                scope,
                document.RootElement.GetProperty("repositories").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray(),
                document.RootElement.GetProperty("validationTargets").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray(),
                document.RootElement.GetProperty("testIntent").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray(),
                document.RootElement.GetProperty("risks").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray(),
                document.RootElement.GetProperty("summary").GetString() ?? "Planning completed.");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private PlanResult BuildFallbackPlan(WorkItemDetails workItem)
    {
        var scope = rulesEngine.ClassifyScope(workItem);
        var repos = MapScopeToRepos(scope).Select(item => item.ToString()).ToArray();
        return new PlanResult(
            scope,
            repos,
            repos.Select(repo => $"{repo} validation").ToArray(),
            ["Keep behavior coherent and cover changed logic."],
            [],
            $"Fallback plan derived from deterministic rules: {scope}.");
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

    private static IReadOnlyList<RepoKind> MapScopeToRepos(WorkItemScope scope) => scope switch
    {
        WorkItemScope.Backend => [RepoKind.Backend],
        WorkItemScope.Frontend => [RepoKind.Frontend],
        _ => [RepoKind.Backend, RepoKind.Frontend],
    };

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

    private sealed record RepositoryExecution(
        ChildTaskRecord ChildTask,
        RepositoryProfile Profile,
        RepoExecutionContext Workspace,
        string ArtifactDirectory)
    {
        public RepoKind RepoKind => Workspace.RepoKind;
    }
}
