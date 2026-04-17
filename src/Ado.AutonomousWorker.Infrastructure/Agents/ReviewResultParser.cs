using System.Text.Json;
using System.Text.Json.Serialization;
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
                    f.FilePath,
                    Category: f.Category,
                    SuggestedFix: f.SuggestedFix))
                .ToList();
            return new ReviewResult(summary, findings, transcriptPath, payload);
        }
        catch (JsonException)
        {
            return new ReviewResult(payload.Trim(), [], transcriptPath, payload);
        }
    }

    private sealed record ReviewPayload(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("findings")] IReadOnlyList<ReviewFindingPayload>? Findings);

    private sealed record ReviewFindingPayload(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("severity")] string? Severity,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("rationale")] string? Rationale,
        [property: JsonPropertyName("suggestedFix")] string? SuggestedFix,
        [property: JsonPropertyName("filePath")] string? FilePath);
}
