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

        var executions = new List<string>();
        foreach (var item in queue)
        {
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
        await RunInternalAsync(executionId, workItem, cancellationToken);
        return executionId;
    }

    public async Task ResumeTaskAsync(string executionId, CancellationToken cancellationToken)
    {
        var execution = await stateStore.GetExecutionAsync(executionId, cancellationToken)
            ?? throw new InvalidOperationException($"Execution '{executionId}' was not found.");

        var workItem = await azureDevOpsClient.GetWorkItemAsync(execution.WorkItemId, cancellationToken);
        await AppendEventAsync(executionId, execution.Phase, "Resuming execution from persisted state.", cancellationToken);
        await RunInternalAsync(executionId, workItem, cancellationToken);
    }

    private async Task RunInternalAsync(string executionId, WorkItemDetails workItem, CancellationToken cancellationToken)
    {
        try
        {
            if (!rulesEngine.IsEligible(workItem, azureDevOpsOptions.Value.DefaultAssignee))
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

            foreach (var repoKind in MapScopeToRepos(plan.Scope))
            {
                var childTask = workItem.ChildTasks.FirstOrDefault(task => task.RepoKind == repoKind) ??
                                await CreateChildTaskAsync(workItem, repoKind, cancellationToken);

                var pr = await ProcessRepositoryAsync(executionId, workItem, childTask, plan, repoKind, cancellationToken);
                await azureDevOpsClient.LinkPullRequestAsync(childTask.WorkItemId, pr, cancellationToken);
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
        }

        if (string.IsNullOrWhiteSpace(workItem.AssignedTo))
        {
            await azureDevOpsClient.AssignWorkItemAsync(workItem.WorkItemId, azureDevOpsOptions.Value.DefaultAssignee, cancellationToken);
        }

        if (workItem.Kind == QueueItemKind.AiWork && !string.IsNullOrWhiteSpace(currentSprint))
        {
            await azureDevOpsClient.UpdateIterationPathAsync(workItem.WorkItemId, currentSprint, cancellationToken);
        }
    }

    private async Task<PlanResult> RunPlanAsync(string executionId, WorkItemDetails workItem, CancellationToken cancellationToken)
    {
        var config = agentOptions.Value.Plan;
        var runner = agentRunnerFactory.GetRunner(config.Provider);
        var artifactDirectory = Path.Combine(storageOptions.Value.ArtifactRoot, executionId, "plan");

        var prompt = $$"""
        Produce a concise implementation plan for this Azure DevOps work item and return JSON matching the provided schema exactly.

        Work item: #{{workItem.WorkItemId}} {{workItem.Title}}
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
            cancellationToken);

        await stateStore.SaveAgentSessionAsync(executionId, startResult.Session, cancellationToken);
        var plan = ParsePlan(startResult.Result.StructuredPayload) ?? BuildFallbackPlan(workItem);
        await AppendEventAsync(executionId, TaskPhase.Planning, plan.Summary, cancellationToken);
        return plan;
    }

    private async Task<PullRequestInfo> ProcessRepositoryAsync(
        string executionId,
        WorkItemDetails parent,
        ChildTaskRecord childTask,
        PlanResult plan,
        RepoKind repoKind,
        CancellationToken cancellationToken)
    {
        var repoProfile = GetRepositoryProfile(repoKind);
        var slug = SlugRegex().Replace(parent.Title.ToLowerInvariant(), "-").Trim('-');
        var workspace = await workspaceManager.CreateRepoExecutionAsync(parent.WorkItemId, childTask.WorkItemId, repoKind, slug, cancellationToken);
        var artifactDirectory = Path.Combine(storageOptions.Value.ArtifactRoot, executionId, repoKind.ToString().ToLowerInvariant());

        await UpdatePhaseAsync(executionId, TaskPhase.Coding, $"Running coding agent for {repoKind}.", cancellationToken);
        var codeRunner = agentRunnerFactory.GetRunner(agentOptions.Value.Code.Provider);
        var codeStart = await codeRunner.StartSessionAsync(
            AgentRole.Code,
            agentOptions.Value.Code,
            BuildCodePrompt(parent, childTask, repoKind, plan),
            workspace.WorkingDirectory,
            Path.Combine(artifactDirectory, "code"),
            outputSchema: null,
            cancellationToken);

        await stateStore.SaveAgentSessionAsync(executionId, codeStart.Session, cancellationToken);

        await UpdatePhaseAsync(executionId, TaskPhase.Reviewing, $"Running review agent for {repoKind}.", cancellationToken);
        var reviewRunner = agentRunnerFactory.GetRunner(agentOptions.Value.Review.Provider);
        var review = await reviewRunner.RunReviewAsync(
            agentOptions.Value.Review,
            BuildReviewPrompt(parent, childTask, repoKind),
            workspace.WorkingDirectory,
            Path.Combine(artifactDirectory, "review"),
            AgentOutputSchemas.Review,
            cancellationToken);

        await stateStore.SaveReviewResultAsync(executionId, review, cancellationToken);
        var acceptedFindings = review.Findings.Where(ShouldAcceptFinding).ToArray();
        if (acceptedFindings.Length > 0)
        {
            await UpdatePhaseAsync(executionId, TaskPhase.ApplyingReview, $"Resuming coding agent for {repoKind} with {acceptedFindings.Length} accepted findings.", cancellationToken);
            await codeRunner.ResumeSessionAsync(
                codeStart.Session,
                agentOptions.Value.Code,
                BuildReviewResumePrompt(acceptedFindings),
                Path.Combine(artifactDirectory, "resume"),
                cancellationToken);
        }

        await UpdatePhaseAsync(executionId, TaskPhase.Validating, $"Running validation for {repoKind}.", cancellationToken);
        foreach (var validationCommand in repoProfile.ValidationCommands)
        {
            var validationResult = await commandRunner.RunPowerShellAsync(
                validationCommand,
                workspace.WorkingDirectory,
                Path.Combine(artifactDirectory, $"{NormalizeFileName(validationCommand)}.log"),
                cancellationToken);

            if (!validationResult.Success)
            {
                throw new InvalidOperationException($"Validation failed for {repoKind}: {validationResult.StandardError}");
            }
        }

        await UpdatePhaseAsync(executionId, TaskPhase.Publishing, $"Creating commit and PR for {repoKind}.", cancellationToken);
        await RunGitAsync(workspace.WorkingDirectory, artifactDirectory, "git-add.log", ["add", "-A"], cancellationToken);
        await RunGitAllowingNoChangesAsync(workspace.WorkingDirectory, artifactDirectory, "git-commit.log", ["commit", "-m", $"#{childTask.WorkItemId}: {parent.Title}"], cancellationToken);
        await RunGitAsync(workspace.WorkingDirectory, artifactDirectory, "git-push.log", ["push", "-u", "origin", workspace.BranchName], cancellationToken);

        return await azureDevOpsClient.CreateDraftPullRequestAsync(
            repoProfile.AzureDevOpsRepository,
            workspace.BranchName,
            $"#{childTask.WorkItemId}: {parent.Title}",
            $"Resolved: #{childTask.WorkItemId}",
            cancellationToken);
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

    private static string BuildCodePrompt(WorkItemDetails parent, ChildTaskRecord childTask, RepoKind repoKind, PlanResult plan)
    {
        return $$"""
        Implement the Azure DevOps task in this repository.

        Parent story: #{{parent.WorkItemId}} {{parent.Title}}
        Child task: #{{childTask.WorkItemId}} {{childTask.Title}}
        Repo: {{repoKind}}
        Plan summary: {{plan.Summary}}

        Requirements:
        - Stay within this repository.
        - Implement the requested behavior.
        - Update or add tests when relevant.
        - Leave the repository in a reviewable state.
        """;
    }

    private static string BuildReviewPrompt(WorkItemDetails parent, ChildTaskRecord childTask, RepoKind repoKind)
    {
        return $$"""
        Review the current repository changes for correctness, regressions, security issues, and missing tests.
        Return JSON that matches the supplied schema exactly.

        Parent story: #{{parent.WorkItemId}} {{parent.Title}}
        Child task: #{{childTask.WorkItemId}} {{childTask.Title}}
        Repo: {{repoKind}}
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

    private static string NormalizeFileName(string value)
    {
        return SlugRegex().Replace(value.ToLowerInvariant(), "-").Trim('-');
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex SlugRegex();
}
