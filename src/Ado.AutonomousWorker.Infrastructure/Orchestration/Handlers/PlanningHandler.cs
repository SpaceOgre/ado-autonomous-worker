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
    ILogger<PlanningHandler> logger
) : WorkItemHandler
{
    public override TaskPhase Phase => TaskPhase.Planning;

    protected override async Task<PhaseTransition> ExecuteAsync(
        PhaseContext.WorkItemContext context,
        CancellationToken cancellationToken
    )
    {
        var workItem = context.WorkItem;
        var config = agentOptions.Value.Plan;
        var runner = agentRunnerFactory.GetRunner(config.Provider);
        var artifactDirectory = Path.Combine(
            storageOptions.Value.GetExecutionArtifactRoot(workItem.WorkItemId, context.ExecutionId),
            "plan"
        );
        var commentsBlock = WorkItemPromptContextFormatter.FormatComments(
            workItem.Comments,
            workerOptions.Value.MaxPromptComments,
            workerOptions.Value.MaxPromptCommentCharacters
        );

        // Plan against a snapshot of origin/main rather than the base clone, which may be a developer's checkout
        // on another branch.
        var repositoryRoot = await workspaceManager.PreparePlanningSnapshotAsync(artifactDirectory, cancellationToken);
        var backendPath = Path.Combine(repositoryRoot, repositoryOptions.Value.Backend.Folder);
        var frontendPath = Path.Combine(repositoryRoot, repositoryOptions.Value.Frontend.Folder);

        await context.Journal.AppendEventAsync(
            context.ExecutionId,
            TaskPhase.Planning,
            "Running planning agent.",
            cancellationToken
        );

        logger.LogDebug(
            "Execution {ExecutionId}: planning with backend '{BackendPath}', frontend '{FrontendPath}'.",
            context.ExecutionId,
            backendPath,
            frontendPath
        );

        var rawPrompt = $$"""
            Produce a concise implementation plan for this Azure DevOps work item and return JSON matching the provided schema exactly.

            <work-item id="{{workItem.WorkItemId}}" type="{{RepositoryWorkflowHelpers.FormatWorkItemLabel(
                workItem.WorkItemType
            )}}" title="{{workItem.Title}}">
            <state>{{workItem.State}}</state>
            <assigned-to>{{workItem.AssignedTo}}</assigned-to>
            <iteration>{{workItem.IterationPath}}</iteration>
            <tags>{{string.Join(", ", workItem.Tags)}}</tags>
            <parent-feature>{{workItem.ParentFeatureTitle ?? "-"}}</parent-feature>
            <description>
            {{workItem.Description}}
            </description>
            <acceptance-criteria>
            {{workItem.AcceptanceCriteria}}
            </acceptance-criteria>
            <recent-comments>
            {{commentsBlock}}
            </recent-comments>
            </work-item>

            ## Step 1: Explore the repository

            The code lives in one monorepo at {{repositoryRoot}} with two halves. Before deciding scope, read its structure to understand the tech stack, existing conventions, and where the relevant code lives. Look at directory layout, key entry points, and test file locations. Start with the AGENTS.md of each half.

            - Backend: {{backendPath}}
            - Frontend: {{frontendPath}}

            ## Step 2: Determine scope

            Choose exactly one value:
            - `Backend` — all changes are server-side only (API, domain logic, database, background jobs).
            - `Frontend` — all changes are client-side only (UI components, styles, browser logic).
            - `Fullstack` — changes span both halves (e.g. new API endpoint consumed by new UI, schema change visible to the frontend).

            Prefer a narrower scope over `Fullstack` when in doubt.

            ## Step 3: Output rules

            Return JSON matching the supplied schema exactly. Field guidance:
            - `scope`: one of `Backend`, `Frontend`, `Fullstack`.
            - `repositories`: absolute paths of the halves that must be modified (only those in scope).
            - `validationTargets`: shell commands that verify the change is correct (build + test commands for each in-scope half). Use commands you observed in the repository (e.g. `dotnet build`, `dotnet test`, `npm run test`).
            - `testIntent`: specific descriptions of tests to add or update. Name the component or function and the behavior to test. Omit entries for trivial getters or pure configuration — only include non-trivial logic.
            - `risks`: open questions or implementation risks that could affect delivery (breaking changes, missing requirements, migration concerns). Empty array if none.
            - `summary`: one or two sentences — what will be built and why. This is shown in the execution log.

            Do not speculate beyond what the acceptance criteria describe.
            """;

        var prompt = await PromptTemplateLoader.ApplyAsync(config, rawPrompt, cancellationToken);
        var startResult = await runner.StartSessionAsync(
            AgentRole.Plan,
            config,
            prompt,
            repositoryRoot,
            artifactDirectory,
            AgentOutputSchemas.Plan,
            executionJournal.CreateOutputObserver(context.ExecutionId),
            cancellationToken
        );

        await stateStore.SaveAgentSessionAsync(context.ExecutionId, startResult.Session, cancellationToken);
        if (startResult.Result.Usage is not null)
            await stateStore.RecordAgentUsageAsync(
                context.ExecutionId,
                TaskPhase.Planning,
                AgentRole.Plan,
                config.Provider,
                startResult.Result.Usage,
                cancellationToken
            );

        var plan =
            ParsePlan(startResult.Result.StructuredPayload, logger)
            ?? throw new InvalidOperationException("Planning agent returned invalid or empty structured output.");

        ValidatePlanRepositories(plan, repositoryRoot);

        var safeSummary = plan.Summary.ReplaceLineEndings(" ");
        await stateStore.SavePlanAsync(context.ExecutionId, plan, cancellationToken);
        await context.Journal.AppendEventAsync(context.ExecutionId, TaskPhase.Planning, safeSummary, cancellationToken);

        logger.LogInformation(
            "Execution {ExecutionId} plan complete: scope {Scope}, repos {Repositories}, testIntent {TestIntentCount} item(s).",
            context.ExecutionId,
            plan.Scope,
            string.Join(", ", plan.Repositories),
            plan.TestIntent.Count
        );

        context.Plan = plan;
        return PhaseTransition.To(TaskPhase.Coding);
    }

    private static PlanResult? ParsePlan(string? payload, ILogger logger)
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
                plan.TestIntent?.Select(t => t.ReplaceLineEndings(" ").Trim()).Where(t => t.Length > 0).ToArray() ?? [],
                plan.Risks?.ToArray() ?? [],
                plan.Summary ?? "Planning completed."
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse planning output. Payload length: {PayloadLength}.", payload.Length);
            return null;
        }
    }

    private static void ValidatePlanRepositories(PlanResult plan, string repositoryRoot)
    {
        foreach (var repo in plan.Repositories)
        {
            if (!repo.StartsWith(repositoryRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Planning agent returned a repository path outside configured roots: '{repo}'."
                );
            }
        }
    }

    private sealed record PlanPayload(
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("repositories")] IReadOnlyList<string>? Repositories,
        [property: JsonPropertyName("testIntent")] IReadOnlyList<string>? TestIntent,
        [property: JsonPropertyName("risks")] IReadOnlyList<string>? Risks,
        [property: JsonPropertyName("summary")] string? Summary
    );
}
