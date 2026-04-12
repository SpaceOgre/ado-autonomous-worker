using System.Text.Json;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Infrastructure.Agents;

internal static class ReviewResultParser
{
    public static ReviewResult Parse(string? payload, string transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new ReviewResult("No review payload was captured.", [], transcriptPath, null);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var summary = document.RootElement.TryGetProperty("summary", out var summaryElement)
                ? summaryElement.GetString() ?? "Review completed."
                : "Review completed.";

            var findings = new List<ReviewFinding>();
            if (document.RootElement.TryGetProperty("findings", out var findingsElement) &&
                findingsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var finding in findingsElement.EnumerateArray())
                {
                    findings.Add(new ReviewFinding(
                        finding.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                        finding.GetProperty("severity").GetString() ?? "unknown",
                        finding.GetProperty("title").GetString() ?? "Untitled finding",
                        finding.GetProperty("rationale").GetString() ?? string.Empty,
                        finding.TryGetProperty("filePath", out var filePathElement) && filePathElement.ValueKind != JsonValueKind.Null
                            ? filePathElement.GetString()
                            : null));
                }
            }

            return new ReviewResult(summary, findings, transcriptPath, payload);
        }
        catch (JsonException)
        {
            return new ReviewResult(payload.Trim(), [], transcriptPath, payload);
        }
    }
}
