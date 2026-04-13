using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal sealed class PhaseContext
{
    public required string ExecutionId { get; init; }
    public required WorkItemDetails WorkItem { get; init; }
    public required bool IsExplicitResume { get; init; }
    public required ExecutionJournal Journal { get; init; }

    // Set by EligibilityHandler
    public string? CurrentSprintPath { get; set; }

    // Set by PlanningHandler; pre-loaded from stateStore on resume when startPhase > Planning
    public PlanResult? Plan { get; set; }

    // Set by CodingHandler via PrepareAsync; rebuilt on every run (PrepareAsync is idempotent)
    public IReadOnlyList<RepositoryExecution> RepositoryExecutions { get; set; } = [];

    // Set by CodingHandler after StartSessionAsync; pre-loaded from stateStore on resume when startPhase >= Reviewing
    public AgentSessionHandle? CodeSession { get; set; }

    // Set by ReviewingHandler; pre-loaded from stateStore on resume when startPhase >= ApplyingReview
    public ReviewResult? LatestReview { get; set; }

    // Set by ReviewingHandler before transitioning to ApplyingReview.
    // Null on resume at ApplyingReview — ApplyingReviewHandler re-derives from LatestReview in that case.
    public IReadOnlyList<ReviewFinding>? AcceptedFindings { get; set; }

    // Populated by PublishingHandler; used for the ADO success comment
    public List<RepositoryPublishResult> PublishedRepositories { get; } = [];
}
