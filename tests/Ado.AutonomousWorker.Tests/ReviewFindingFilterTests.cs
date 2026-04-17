using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

namespace Ado.AutonomousWorker.Tests;

public sealed class ReviewFindingFilterTests
{
    private static ReviewFinding Finding(string severity, string? category = null) =>
        new("id", severity, "Title", "Rationale", Category: category);

    [Theory]
    [InlineData("critical")]
    [InlineData("high")]
    public void ShouldAccept_AcceptsCriticalAndHighRegardlessOfCategory(string severity)
    {
        Assert.True(ReviewFindingFilter.ShouldAccept(Finding(severity)));
    }

    [Theory]
    [InlineData("medium", "bugs")]
    [InlineData("medium", "security")]
    [InlineData("medium", "error-handling")]
    [InlineData("medium", "BUGS")]
    public void ShouldAccept_AcceptsMediumActionableCategory(string severity, string category)
    {
        Assert.True(ReviewFindingFilter.ShouldAccept(Finding(severity, category)));
    }

    [Fact]
    public void ShouldAccept_RejectsLowSeverityBugsFinding()
    {
        Assert.False(ReviewFindingFilter.ShouldAccept(Finding("low", "bugs")));
    }

    [Fact]
    public void ShouldAccept_RejectsLowSeveritySecurityFinding()
    {
        Assert.False(ReviewFindingFilter.ShouldAccept(Finding("low", "security")));
    }

    [Theory]
    [InlineData("medium", "test-coverage")]
    [InlineData("low", "test-coverage")]
    public void ShouldAccept_RejectsTestCoverageViaCategoryPath(string severity, string category)
    {
        // test-coverage is excluded from ActionableCategories; only accepted via keyword fallback
        Assert.False(ReviewFindingFilter.ShouldAccept(Finding(severity, category)));
    }

    [Fact]
    public void ShouldAccept_AcceptsViaKeywordFallback()
    {
        var finding = new ReviewFinding("id", "medium", "Missing test for edge case", "No test covers null input");
        Assert.True(ReviewFindingFilter.ShouldAccept(finding));
    }

    [Fact]
    public void ShouldAccept_RejectsMediumFindingWithNoCategory()
    {
        Assert.False(ReviewFindingFilter.ShouldAccept(Finding("medium")));
    }
}
