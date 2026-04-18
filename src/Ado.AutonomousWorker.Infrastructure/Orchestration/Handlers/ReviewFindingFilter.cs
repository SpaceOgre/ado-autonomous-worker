using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal static class ReviewFindingFilter
{
    // test-coverage is intentionally excluded: missing-test findings are caught by the keyword
    // fallback below, keeping them subject to the same severity gate as unstructured findings.
    private static readonly HashSet<string> ActionableCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bugs",
        "security",
        "error-handling",
    };

    public static bool ShouldAccept(ReviewFinding finding)
    {
        var severity = finding.Severity.ToLowerInvariant();
        if (severity.Contains("critical") || severity.Contains("high"))
            return true;

        if (
            finding.Category is not null
            && ActionableCategories.Contains(finding.Category)
            && !severity.Contains("low")
        )
            return true;

        var text = $"{finding.Title} {finding.Rationale}".ToLowerInvariant();
        return text.Contains("bug")
            || text.Contains("security")
            || text.Contains("correctness")
            || text.Contains("missing test");
    }
}
