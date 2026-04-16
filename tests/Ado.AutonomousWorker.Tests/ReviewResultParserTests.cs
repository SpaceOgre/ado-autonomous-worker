using Ado.AutonomousWorker.Infrastructure.Agents;

namespace Ado.AutonomousWorker.Tests;

public sealed class ReviewResultParserTests
{
    private const string TranscriptPath = "/tmp/review.log";

    // ── ParseFreeText: no-issue cases ────────────────────────────────────────

    [Fact]
    public void ParseFreeText_NullInput_ReturnsEmptyFindings()
    {
        var result = ReviewResultParser.ParseFreeText(null, TranscriptPath);

        Assert.Empty(result.Findings);
        Assert.Contains("No review output", result.Summary);
    }

    [Fact]
    public void ParseFreeText_EmptyInput_ReturnsEmptyFindings()
    {
        var result = ReviewResultParser.ParseFreeText("   ", TranscriptPath);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void ParseFreeText_NoIssuesFoundText_ReturnsEmptyFindings()
    {
        const string text = "No issues found. The diff looks clean across all 12 review angles (bugs, security, architecture, tests, error handling, performance, complexity, observability, API contracts, data/migrations, accessibility, type safety).";

        var result = ReviewResultParser.ParseFreeText(text, TranscriptPath);

        Assert.Empty(result.Findings);
        Assert.StartsWith("No issues found", result.Summary);
    }

    // ── ParseFreeText: severity mapping ──────────────────────────────────────

    [Fact]
    public void ParseFreeText_BlockingSection_MapsToCriticalSeverity()
    {
        const string text = """
            ### 🔴 Blocking issues (must fix)

            - **#1 [src/Foo.cs:42]** Null dereference on user input
              - The value is never validated before use
              - NullReferenceException at runtime
              - Severity: BLOCKING
            """;

        var result = ReviewResultParser.ParseFreeText(text, TranscriptPath);

        Assert.Single(result.Findings);
        Assert.Equal("critical", result.Findings[0].Severity);
        Assert.Equal("finding-1", result.Findings[0].Id);
        Assert.Equal("Null dereference on user input", result.Findings[0].Title);
        Assert.Equal("src/Foo.cs", result.Findings[0].FilePath);
    }

    [Fact]
    public void ParseFreeText_ImportantSection_MapsToHighSeverity()
    {
        const string text = """
            ### 🟠 Important issues

            - **#2 [src/Bar.cs:100]** Missing error boundary
              - Unhandled exceptions escape to top level
              - Service crashes in production
              - Severity: IMPORTANT
            """;

        var result = ReviewResultParser.ParseFreeText(text, TranscriptPath);

        Assert.Single(result.Findings);
        Assert.Equal("high", result.Findings[0].Severity);
        Assert.Equal("src/Bar.cs", result.Findings[0].FilePath);
    }

    [Fact]
    public void ParseFreeText_MinorSection_MapsToLowSeverity()
    {
        const string text = """
            ### 🟡 Minor issues

            - **#3 [src/Baz.cs:200]** Unnecessary allocation in tight loop
              - Minor memory overhead
              - Slight performance regression at scale
              - Severity: MINOR
            """;

        var result = ReviewResultParser.ParseFreeText(text, TranscriptPath);

        Assert.Single(result.Findings);
        Assert.Equal("low", result.Findings[0].Severity);
    }

    // ── ParseFreeText: multi-section output ───────────────────────────────────

    [Fact]
    public void ParseFreeText_AllThreeSections_ParsesAllFindings()
    {
        const string text = """
            ### 🔴 Blocking issues (must fix)

            - **#1 [auth/Login.cs:15]** SQL injection via unsanitised input
              - Raw string concatenation into query
              - Full database compromise

            ### 🟠 Important issues

            - **#2 [services/EmailSender.cs:88]** Missing retry on transient failure
              - No retry logic exists
              - Emails silently lost on network hiccup

            ### 🟡 Minor issues

            - **#3 [utils/StringHelper.cs:3]** Method name does not follow conventions
              - Should be PascalCase
              - Low impact
            """;

        var result = ReviewResultParser.ParseFreeText(text, TranscriptPath);

        Assert.Equal(3, result.Findings.Count);
        Assert.Equal("critical", result.Findings[0].Severity);
        Assert.Equal("high", result.Findings[1].Severity);
        Assert.Equal("low", result.Findings[2].Severity);
        Assert.Equal("auth/Login.cs", result.Findings[0].FilePath);
        Assert.Equal("services/EmailSender.cs", result.Findings[1].FilePath);
        Assert.Equal("utils/StringHelper.cs", result.Findings[2].FilePath);
    }

    // ── ParseFreeText: rationale and "Flagged by" handling ───────────────────

    [Fact]
    public void ParseFreeText_FlaggedByLine_IsExcludedFromRationale()
    {
        const string text = """
            ### 🔴 Blocking issues (must fix)

            - **#1 [src/Foo.cs:1]** Race condition in shared state
              - Concurrent writes without lock
              - Data corruption under load
              - *Flagged by: Bug Hunter, Security Reviewer*
            """;

        var result = ReviewResultParser.ParseFreeText(text, TranscriptPath);

        Assert.Single(result.Findings);
        Assert.DoesNotContain("Flagged by", result.Findings[0].Rationale);
    }

    [Fact]
    public void ParseFreeText_RationaleLines_AreConcatenatedIntoRationale()
    {
        const string text = """
            ### 🟠 Important issues

            - **#1 [src/Foo.cs:55]** Unbounded cache growth
              - Cache is never evicted
              - Memory leak under sustained load
            """;

        var result = ReviewResultParser.ParseFreeText(text, TranscriptPath);

        Assert.Single(result.Findings);
        var rationale = result.Findings[0].Rationale;
        Assert.Contains("evicted", rationale);
        Assert.Contains("Memory leak", rationale);
    }

    // ── ParseFreeText: file path extraction ───────────────────────────────────

    [Fact]
    public void ParseFreeText_FileRefWithLineNumber_StripsLineNumberFromFilePath()
    {
        const string text = """
            ### 🔴 Blocking issues (must fix)

            - **#1 [src/deep/nested/File.cs:999]** Some issue
              - rationale
            """;

        var result = ReviewResultParser.ParseFreeText(text, TranscriptPath);

        Assert.Equal("src/deep/nested/File.cs", result.Findings[0].FilePath);
    }

    [Fact]
    public void ParseFreeText_FileRefWithoutLineNumber_UsesFullRefAsFilePath()
    {
        const string text = """
            ### 🟠 Important issues

            - **#1 [README.md]** Outdated documentation
              - rationale
            """;

        var result = ReviewResultParser.ParseFreeText(text, TranscriptPath);

        Assert.Equal("README.md", result.Findings[0].FilePath);
    }

    // ── ParseFreeText: summary generation ────────────────────────────────────

    [Fact]
    public void ParseFreeText_MixedFindings_SummaryIncludesCounts()
    {
        const string text = """
            ### 🔴 Blocking issues (must fix)

            - **#1 [a.cs:1]** Blocking issue
              - rationale

            ### 🟠 Important issues

            - **#2 [b.cs:2]** Important issue
              - rationale

            - **#3 [c.cs:3]** Another important issue
              - rationale
            """;

        var result = ReviewResultParser.ParseFreeText(text, TranscriptPath);

        Assert.Equal(3, result.Findings.Count);
        Assert.Contains("1 blocking", result.Summary);
        Assert.Contains("2 important", result.Summary);
    }

    // ── ParseFreeText: TranscriptPath is preserved ────────────────────────────

    [Fact]
    public void ParseFreeText_TranscriptPath_IsPreservedInResult()
    {
        var result = ReviewResultParser.ParseFreeText("No issues found.", "/custom/path/review.log");

        Assert.Equal("/custom/path/review.log", result.TranscriptPath);
    }
}
