using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record ReviewFinding(
    string Id,
    string Severity,
    string Title,
    string Rationale,
    string? FilePath = null,
    ReviewDisposition Disposition = ReviewDisposition.Pending,
    string? Category = null,
    string? SuggestedFix = null
);
