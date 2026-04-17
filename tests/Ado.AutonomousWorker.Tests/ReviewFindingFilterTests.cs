using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

namespace Ado.AutonomousWorker.Tests;

public sealed class ReviewFindingFilterTests
{
    private static ReviewFinding Finding(string severity, string? category = null) =>
        new("id", severity, "Title", "Rationale", Category: category);

    [Test]
    [Arguments("critical")]
    [Arguments("high")]
    public async Task ShouldAccept_AcceptsCriticalAndHighRegardlessOfCategory(string severity)
    {
        await Assert.That(ReviewFindingFilter.ShouldAccept(Finding(severity))).IsTrue();
    }

    [Test]
    [Arguments("medium", "bugs")]
    [Arguments("medium", "security")]
    [Arguments("medium", "error-handling")]
    [Arguments("medium", "BUGS")]
    public async Task ShouldAccept_AcceptsMediumActionableCategory(string severity, string category)
    {
        await Assert.That(ReviewFindingFilter.ShouldAccept(Finding(severity, category))).IsTrue();
    }

    [Test]
    public async Task ShouldAccept_RejectsLowSeverityBugsFinding()
    {
        await Assert.That(ReviewFindingFilter.ShouldAccept(Finding("low", "bugs"))).IsFalse();
    }

    [Test]
    public async Task ShouldAccept_RejectsLowSeveritySecurityFinding()
    {
        await Assert.That(ReviewFindingFilter.ShouldAccept(Finding("low", "security"))).IsFalse();
    }

    [Test]
    [Arguments("medium", "test-coverage")]
    [Arguments("low", "test-coverage")]
    public async Task ShouldAccept_RejectsTestCoverageViaCategoryPath(string severity, string category)
    {
        // test-coverage is excluded from ActionableCategories; only accepted via keyword fallback
        await Assert.That(ReviewFindingFilter.ShouldAccept(Finding(severity, category))).IsFalse();
    }

    [Test]
    public async Task ShouldAccept_AcceptsViaKeywordFallback()
    {
        var finding = new ReviewFinding("id", "medium", "Missing test for edge case", "No test covers null input");
        await Assert.That(ReviewFindingFilter.ShouldAccept(finding)).IsTrue();
    }

    [Test]
    public async Task ShouldAccept_RejectsMediumFindingWithNoCategory()
    {
        await Assert.That(ReviewFindingFilter.ShouldAccept(Finding("medium"))).IsFalse();
    }
}
