using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal static class ReviewFindingFilter
{
    public static bool ShouldAccept(ReviewFinding finding)
    {
        var severity = finding.Severity.ToLowerInvariant();
        if (severity.Contains("critical") || severity.Contains("high"))
            return true;

        var text = $"{finding.Title} {finding.Rationale}".ToLowerInvariant();
        return text.Contains("bug") || text.Contains("security") ||
               text.Contains("correctness") || text.Contains("missing test");
    }
}
