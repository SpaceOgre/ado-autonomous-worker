using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

/// <summary>Identifies the pull request to be targeted by an AiPrFix execution.</summary>
internal sealed record PrTarget(int PullRequestId, string RepositoryName, string BranchName);

internal abstract record PhaseContext
{
    public required string ExecutionId { get; init; }
    public required WorkItemDetails WorkItem { get; init; }
    public required bool IsExplicitResume { get; init; }
    public required ExecutionJournal Journal { get; init; }

    // Mutable state written by handlers during the pipeline
    public IReadOnlyList<RepositoryExecution> RepositoryExecutions { get; set; } = [];
    public AgentSessionHandle? CodeSession { get; set; }
    public ReviewResult? LatestReview { get; set; }
    public IReadOnlyList<ReviewFinding>? AcceptedFindings { get; set; }
    public List<RepositoryPublishResult> PublishedRepositories { get; } = [];
    public int ValidationRetryCount { get; set; }
    public IReadOnlyList<ValidationFailure> ValidationFailures { get; set; } = [];

    /// <summary>
    /// Full AI Work / AI Rework flow: Planning → Coding → Reviewing → ApplyingReview → Validating → Publishing.
    /// </summary>
    internal sealed record WorkItemContext : PhaseContext
    {
        /// <summary>When set, overrides the current-sprint lookup for backlog items.</summary>
        public string? IterationOverride { get; init; }

        /// <summary>Set by EligibilityHandler.</summary>
        public string? CurrentSprintPath { get; set; }

        /// <summary>Set by PlanningHandler; pre-loaded from stateStore on resume when startPhase > Planning.</summary>
        public PlanResult? Plan { get; set; }
    }

    /// <summary>
    /// PR Fix flow: Coding → Validating → Publishing. Planning and Reviewing are skipped.
    /// </summary>
    internal sealed record PrFixContext : PhaseContext
    {
        /// <summary>Identifies the pull request being fixed.</summary>
        public required PrTarget PrTarget { get; init; }

        /// <summary>Set by EligibilityHandler — active review threads to address.</summary>
        public IReadOnlyList<PrReviewThread> PrThreads { get; set; } = [];

        /// <summary>Set by CodingHandler (from structured output); used by PublishingHandler for the commit.</summary>
        public string? CommitMessage { get; set; }
    }
}
