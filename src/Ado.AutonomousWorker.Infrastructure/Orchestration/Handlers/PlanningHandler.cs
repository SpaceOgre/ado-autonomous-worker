using System.Text.Json;
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
    ExecutionJournal executionJournal,
    IOptions<AgentOptions> agentOptions,
    IOptions<WorkerOptions> workerOptions,
    IOptions<StorageOptions> storageOptions,
    ILogger<PlanningHandler> logger) : IPhaseHandler
{
    public TaskPhase Phase => TaskPhase.Planning;

    public async Task<PhaseTransition> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken)
    {
        var workItem = context.WorkItem;
        var config = agentOptions.Value.Plan;
        var runner = agentRunnerFactory.GetRunner(config.Provider);
        var artifactDirectory = Path.Combine(
            storageOptions.Value.GetExecutionArtifactRoot(workItem.WorkItemId, context.ExecutionId), "plan");
        var commentsBlock = WorkItemPromptContextFormatter.FormatComments(
            workItem.Comments, workerOptions.Value.MaxPromptComments, workerOptions.Value.MaxPromptCommentCharacters);

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

        var prompt = await PromptTemplateLoader.ApplyAsync(config, rawPrompt, cancellationToken);
        var startResult = await runner.StartSessionAsync(
            AgentRole.Plan,
            config,
            prompt,
            Directory.GetCurrentDirectory(),
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
            using var document = JsonDocument.Parse(payload);
            var scope = Enum.Parse<WorkItemScope>(
                document.RootElement.GetProperty("scope").GetString() ?? "Fullstack", ignoreCase: true);

            return new PlanResult(
                scope,
                document.RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray(),
                document.RootElement.GetProperty("validationTargets").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray(),
                document.RootElement.GetProperty("testIntent").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray(),
                document.RootElement.GetProperty("risks").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray(),
                document.RootElement.GetProperty("summary").GetString() ?? "Planning completed.");
        }
        catch (Exception)
        {
            return null;
        }
    }
}
