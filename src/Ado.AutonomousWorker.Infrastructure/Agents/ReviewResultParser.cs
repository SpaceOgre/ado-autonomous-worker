using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Infrastructure.Agents;

internal static class ReviewResultParser
{
    public static ReviewResult Parse(string? payload, string transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return new ReviewResult("No review payload was captured.", [], transcriptPath, null);

        try
        {
            var review = JsonSerializer.Deserialize<ReviewPayload>(payload);
            var summary = review?.Summary ?? "Review completed.";
            var findings = (review?.Findings ?? [])
                .Select(f => new ReviewFinding(
                    f.Id ?? Guid.NewGuid().ToString("N"),
                    f.Severity ?? "unknown",
                    f.Title ?? "Untitled finding",
                    f.Rationale ?? string.Empty,
                    f.FilePath))
                .ToList();
            return new ReviewResult(summary, findings, transcriptPath, payload);
        }
        catch (JsonException)
        {
            return new ReviewResult(payload.Trim(), [], transcriptPath, payload);
        }
    }

    /// <summary>
    /// Parses the free-text markdown output produced by the hardcore-code-reviewer Claude plugin.
    /// Sections are delimited by 🔴 (blocking → "critical"), 🟠 (important → "high"), and
    /// 🟡 (minor → "low") headers. Each finding line starts with <c>- **#N [file:line]** Title</c>.
    /// </summary>
    public static ReviewResult ParseFreeText(string? text, string transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ReviewResult("No review output captured.", [], transcriptPath, null);

        var trimmed = text.Trim();

        // "No issues found." short-circuit
        if (trimmed.StartsWith("No issues found", StringComparison.OrdinalIgnoreCase))
            return new ReviewResult(trimmed.Split('\n')[0].Trim(), [], transcriptPath, null);

        var findings = ParseFindings(trimmed);
        var summary = BuildSummary(findings);
        return new ReviewResult(summary, findings, transcriptPath, null);
    }

    // Finding pattern: - **#N [file:line]** Title
    private static readonly Regex FindingStartPattern = new(
        @"^\s*-\s+\*\*#(\d+)\s+\[([^\]]+)\]\*\*\s+(.+)$",
        RegexOptions.Compiled);

    private static List<ReviewFinding> ParseFindings(string text)
    {
        var findings = new List<ReviewFinding>();
        var currentSeverity = "low";

        string? currentId = null;
        string? currentTitle = null;
        string? currentFileRef = null;
        var rationaleLines = new List<string>();

        void FlushFinding()
        {
            if (currentTitle is null)
                return;

            var filePath = ParseFilePath(currentFileRef);
            var rationale = string.Join(" ", rationaleLines);
            findings.Add(new ReviewFinding(
                currentId ?? Guid.NewGuid().ToString("N"),
                currentSeverity,
                currentTitle,
                rationale,
                filePath));

            currentId = null;
            currentTitle = null;
            currentFileRef = null;
            rationaleLines.Clear();
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd();

            // Severity section headers
            if (IsBlockingHeader(line)) { FlushFinding(); currentSeverity = "critical"; continue; }
            if (IsImportantHeader(line)) { FlushFinding(); currentSeverity = "high"; continue; }
            if (IsMinorHeader(line)) { FlushFinding(); currentSeverity = "low"; continue; }

            // Finding start: - **#N [file:line]** Title
            var match = FindingStartPattern.Match(line);
            if (match.Success)
            {
                FlushFinding();
                currentId = $"finding-{match.Groups[1].Value}";
                currentFileRef = match.Groups[2].Value.Trim();
                currentTitle = match.Groups[3].Value.Trim();
                continue;
            }

            // Sub-bullet lines contribute to rationale
            if (currentTitle is not null)
            {
                var stripped = line.TrimStart();
                if (stripped.StartsWith('-') || stripped.StartsWith('*'))
                {
                    var content = stripped.TrimStart('-', '*', ' ').Trim('*').Trim();
                    // Skip "Flagged by:" attribution lines
                    if (!content.StartsWith("Flagged by:", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(content))
                    {
                        rationaleLines.Add(content);
                    }
                }
            }
        }

        FlushFinding();
        return findings;
    }

    private static bool IsBlockingHeader(string line) =>
        line.Contains("🔴") ||
        (line.Contains("###") && line.Contains("Blocking", StringComparison.OrdinalIgnoreCase));

    private static bool IsImportantHeader(string line) =>
        line.Contains("🟠") ||
        (line.Contains("###") && line.Contains("Important", StringComparison.OrdinalIgnoreCase));

    private static bool IsMinorHeader(string line) =>
        line.Contains("🟡") ||
        (line.Contains("###") && line.Contains("Minor", StringComparison.OrdinalIgnoreCase));

    private static string? ParseFilePath(string? fileRef)
    {
        if (string.IsNullOrWhiteSpace(fileRef))
            return null;

        // Strip optional ":lineNumber" suffix to get just the file path
        var lastColon = fileRef.LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(fileRef.AsSpan(lastColon + 1), out _))
            return fileRef[..lastColon];

        return fileRef;
    }

    private static string BuildSummary(IReadOnlyList<ReviewFinding> findings)
    {
        if (findings.Count == 0)
            return "No issues found.";

        var blocking = findings.Count(f => f.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase));
        var important = findings.Count(f => f.Severity.Equals("high", StringComparison.OrdinalIgnoreCase));
        var minor = findings.Count(f => f.Severity.Equals("low", StringComparison.OrdinalIgnoreCase));

        var parts = new List<string>();
        if (blocking > 0) parts.Add($"{blocking} blocking");
        if (important > 0) parts.Add($"{important} important");
        if (minor > 0) parts.Add($"{minor} minor");

        return $"{findings.Count} issue(s) found: {string.Join(", ", parts)}.";
    }

    private sealed record ReviewPayload(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("findings")] IReadOnlyList<ReviewFindingPayload>? Findings);

    private sealed record ReviewFindingPayload(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("severity")] string? Severity,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("rationale")] string? Rationale,
        [property: JsonPropertyName("filePath")] string? FilePath);
}
