using System.Text;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal static class WorkItemCommentFormatter
{
    public static string BuildSuccessComment(
        string executionId,
        IReadOnlyList<RepositoryPublishResult> repositories,
        ImplementationRunResult implementation)
    {
        var builder = new StringBuilder()
            .AppendLine($"Execution {executionId} completed successfully.")
            .AppendLine()
            .AppendLine("Repositories:");

        foreach (var repository in repositories.OrderBy(item => item.RepoKind))
        {
            builder.AppendLine(
                $"- {repository.RepoKind}: child task #{repository.ChildTask.WorkItemId}, branch `{repository.BranchName}`, commit `{repository.CommitSha}`, draft PR #{repository.PullRequest.PullRequestId} ({repository.PullRequest.Url})");
            builder.AppendLine($"  Validation: {FormatList(repository.ValidationCommands)}");
        }

        var reviewSummary = implementation.AcceptedFindingCount.HasValue
            ? $"Review: {implementation.Review.Findings.Count} findings, {implementation.AcceptedFindingCount.Value} accepted, transcript `{implementation.Review.TranscriptPath}`."
            : $"Review transcript: `{implementation.Review.TranscriptPath}`.";

        builder.AppendLine()
            .AppendLine(reviewSummary);

        return builder.ToString().TrimEnd();
    }

    public static string BuildFailureComment(string executionId, Exception exception)
    {
        var builder = new StringBuilder()
            .AppendLine($"Execution {executionId} failed.");

        if (exception is ValidationFailureException validationFailure)
        {
            var output = FirstNonEmpty(validationFailure.StandardError, validationFailure.StandardOutput);
            builder.AppendLine()
                .AppendLine($"Validation failed for {validationFailure.RepoKind}.")
                .AppendLine($"Command: `{validationFailure.Command}`")
                .AppendLine($"Log: `{validationFailure.ArtifactPath}`");

            if (!string.IsNullOrWhiteSpace(output))
            {
                builder.AppendLine("Output:")
                    .AppendLine("```text")
                    .AppendLine(TrimToMaxLength(output, 1500))
                    .AppendLine("```");
            }
        }
        else
        {
            builder.AppendLine()
                .AppendLine($"Reason: {exception.Message}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatList(IReadOnlyList<string> items)
        => items.Count == 0 ? "-" : string.Join(" | ", items);

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string TrimToMaxLength(string value, int maxLength)
        => value.Length <= maxLength ? value.Trim() : $"{value[..maxLength].TrimEnd()}...";
}
