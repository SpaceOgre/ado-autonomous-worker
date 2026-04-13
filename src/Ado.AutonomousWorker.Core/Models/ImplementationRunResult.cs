namespace Ado.AutonomousWorker.Core.Models;

public sealed record ImplementationRunResult(
    ReviewResult Review,
    int? AcceptedFindingCount);
