namespace Ado.AutonomousWorker.Core.Models;

public sealed record ReviewResult(
    string Summary,
    IReadOnlyList<ReviewFinding> Findings,
    string TranscriptPath,
    string? StructuredPayload = null);
