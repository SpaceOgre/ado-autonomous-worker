using System.Text.Json;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Services;
using Ado.AutonomousWorker.Infrastructure.Agents;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal sealed class TaskPlanningService(
    IRulesEngine rulesEngine,
    IStateStore stateStore,
    IAgentRunnerFactory agentRunnerFactory,
    ExecutionJournal executionJournal,
    IOptions<AgentOptions> agentOptions,
    IOptions<StorageOptions> storageOptions,
    ILogger<TaskPlanningService> logger)
{
    public async Task<PlanResult> RunPlanAsync(string executionId, WorkItemDetails workItem, CancellationToken cancellationToken)
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
            executionJournal.CreateOutputObserver(executionId),
            cancellationToken);

        await stateStore.SaveAgentSessionAsync(executionId, startResult.Session, cancellationToken);
        var plan = ParsePlan(startResult.Result.StructuredPayload) ?? BuildFallbackPlan(workItem);
        await stateStore.SavePlanAsync(executionId, plan, cancellationToken);
        await executionJournal.AppendEventAsync(executionId, TaskPhase.Planning, plan.Summary, cancellationToken);

        logger.LogInformation(
            "Execution {ExecutionId} plan complete: scope {Scope}, repos {Repositories}.",
            executionId,
            plan.Scope,
            string.Join(", ", plan.Repositories));
        return plan;
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

    private static IReadOnlyList<RepoKind> MapScopeToRepos(WorkItemScope scope) => scope switch
    {
        WorkItemScope.Backend => [RepoKind.Backend],
        WorkItemScope.Frontend => [RepoKind.Frontend],
        _ => [RepoKind.Backend, RepoKind.Frontend],
    };

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
}
