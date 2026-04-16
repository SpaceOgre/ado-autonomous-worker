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

internal sealed class PlanningHandler(
    IStateStore stateStore,
    IAgentRunnerFactory agentRunnerFactory,
    IWorkspaceManager workspaceManager,
    ExecutionJournal executionJournal,
    IOptions<AgentOptions> agentOptions,
    IOptions<WorkerOptions> workerOptions,
    IOptions<RepositoryOptions> repositoryOptions,
    IOptions<StorageOptions> storageOptions,
    ILogger<PlanningHandler> logger) : WorkItemHandler
{
    public override TaskPhase Phase => TaskPhase.Planning;

    protected override async Task<PhaseTransition> ExecuteAsync(PhaseContext.WorkItemContext context, CancellationToken cancellationToken)
    {
        var workItem = context.WorkItem;
        var config = agentOptions.Value.Plan;
        var runner = agentRunnerFactory.GetRunner(config.Provider);
        var artifactDirectory = Path.Combine(
            storageOptions.Value.GetExecutionArtifactRoot(workItem.WorkItemId, context.ExecutionId), "plan");
        var commentsBlock = WorkItemPromptContextFormatter.FormatComments(
            workItem.Comments, workerOptions.Value.MaxPromptComments, workerOptions.Value.MaxPromptCommentCharacters);

        await workspaceManager.PullAllBaseClonesAsync(artifactDirectory, cancellationToken);

        await context.Journal.AppendEventAsync(
            context.ExecutionId, TaskPhase.Planning, "Running planning agent.", cancellationToken);

        var rawPrompt = $$"""
        Produce a concise implementation plan for this Azure DevOps work item and return JSON matching the provided schema exactly.

        Work item: #{{workItem.WorkItemId}} {{RepositoryWorkflowHelpers.FormatWorkItemLabel(workItem.WorkItemType)}} {{workItem.Title}}
        State: {{workItem.State}}
        Assigned to: {{workItem.AssignedTo}}
        Iteration: {{workItem.IterationPath}}
        Tags: {{string.Join(", ", workItem.Tags)}}
        Parent feature: {{workItem.ParentFeatureTitle ?? "-"}}

        Description:
        {{workItem.Description}}

        Acceptance Criteria:
        {{workItem.AcceptanceCriteria}}

        Recent comments:
        {{commentsBlock}}
        """;

        var agentWorkingDirectory = RepositoryWorkflowHelpers.DetermineAgentWorkingDirectory(
            [repositoryOptions.Value.Backend.BaseClonePath, repositoryOptions.Value.Frontend.BaseClonePath]);

        var prompt = await PromptTemplateLoader.ApplyAsync(config, rawPrompt, cancellationToken);
        var startResult = await runner.StartSessionAsync(
            AgentRole.Plan,
            config,
            prompt,
            agentWorkingDirectory,
            artifactDirectory,
            AgentOutputSchemas.Plan,
            executionJournal.CreateOutputObserver(context.ExecutionId),
            cancellationToken);

        await stateStore.SaveAgentSessionAsync(context.ExecutionId, startResult.Session, cancellationToken);
        if (startResult.Result.Usage is not null)
            await stateStore.RecordAgentUsageAsync(context.ExecutionId, TaskPhase.Planning, AgentRole.Plan, config.Provider, startResult.Result.Usage, cancellationToken);

        var plan = ParsePlan(startResult.Result.StructuredPayload)
            ?? throw new InvalidOperationException("Planning agent returned invalid or empty structured output.");

        await stateStore.SavePlanAsync(context.ExecutionId, plan, cancellationToken);
        await context.Journal.AppendEventAsync(
            context.ExecutionId, TaskPhase.Planning, plan.Summary, cancellationToken);

        logger.LogInformation(
            "Execution {ExecutionId} plan complete: scope {Scope}, repos {Repositories}.",
            context.ExecutionId, plan.Scope, string.Join(", ", plan.Repositories));

        context.Plan = plan;
        return PhaseTransition.To(TaskPhase.Coding);
    }

    private static PlanResult? ParsePlan(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            var plan = JsonSerializer.Deserialize<PlanPayload>(payload);
            if (plan is null)
                return null;

            var scope = Enum.Parse<WorkItemScope>(plan.Scope ?? "Fullstack", ignoreCase: true);
            return new PlanResult(
                scope,
                plan.Repositories?.ToArray() ?? [],
                plan.ValidationTargets?.ToArray() ?? [],
                plan.TestIntent?.ToArray() ?? [],
                plan.Risks?.ToArray() ?? [],
                plan.Summary ?? "Planning completed.");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record PlanPayload(
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("repositories")] IReadOnlyList<string>? Repositories,
        [property: JsonPropertyName("validationTargets")] IReadOnlyList<string>? ValidationTargets,
        [property: JsonPropertyName("testIntent")] IReadOnlyList<string>? TestIntent,
        [property: JsonPropertyName("risks")] IReadOnlyList<string>? Risks,
        [property: JsonPropertyName("summary")] string? Summary);
}
