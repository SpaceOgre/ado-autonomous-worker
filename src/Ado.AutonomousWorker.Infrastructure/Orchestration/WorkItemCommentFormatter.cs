using System.Text;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal static class WorkItemCommentFormatter
{
    public static string BuildSuccessComment(
        string executionId,
        IReadOnlyList<RepositoryPublishResult> repositories,
        ImplementationRunResult implementation
    )
    {
        var shortId = executionId.Length > 8 ? executionId[..8] : executionId;
        var builder = new StringBuilder().AppendLine($"Execution `{shortId}` completed successfully.").AppendLine();

        // Every half is published in the same commit and PR of the monorepo.
        var published = repositories.FirstOrDefault();
        if (published is not null)
        {
            var shortSha = published.CommitSha.Length > 8 ? published.CommitSha[..8] : published.CommitSha;
            builder
                .AppendLine($"Branch: `{published.BranchName}`")
                .AppendLine($"Commit: `{shortSha}`")
                .AppendLine($"PR: !{published.PullRequest.PullRequestId}")
                .AppendLine();
        }

        foreach (var repository in repositories.OrderBy(item => item.RepoKind))
        {
            builder
                .AppendLine($"### {repository.RepoKind}")
                .AppendLine()
                .AppendLine($"Task: #{repository.ChildTask.WorkItemId}");

            if (repository.ValidationCommands.Count > 0)
                builder.AppendLine($"Validation: `{string.Join(" | ", repository.ValidationCommands)}`");

            builder.AppendLine();
        }

        builder.AppendLine("### Review").AppendLine();

        if (implementation.AcceptedFindingCount.HasValue)
            builder.AppendLine(
                $"{implementation.Review.Findings.Count} findings, {implementation.AcceptedFindingCount.Value} accepted."
            );
        else if (implementation.Review.Findings.Count > 0)
            builder.AppendLine($"{implementation.Review.Findings.Count} findings.");

        var hasMeaningfulSummary =
            !string.IsNullOrWhiteSpace(implementation.Review.Summary)
            && !implementation.Review.Summary.Equals("No review available.", StringComparison.OrdinalIgnoreCase);

        if (hasMeaningfulSummary)
            builder.AppendLine(implementation.Review.Summary);
        else if (!implementation.AcceptedFindingCount.HasValue && implementation.Review.Findings.Count == 0)
            builder.AppendLine("No review.");

        return builder.ToString().TrimEnd();
    }

    public static string BuildFailureComment(string executionId, Exception exception)
    {
        var builder = new StringBuilder().AppendLine($"Execution {executionId} failed.");

        if (exception is ValidationFailureException validationFailure)
        {
            var output = FirstNonEmpty(validationFailure.StandardError, validationFailure.StandardOutput);
            builder
                .AppendLine()
                .AppendLine($"Validation failed for {validationFailure.RepoKind}.")
                .AppendLine($"Command: `{validationFailure.Command}`")
                .AppendLine($"Log: `{validationFailure.ArtifactPath}`");

            if (!string.IsNullOrWhiteSpace(output))
            {
                builder
                    .AppendLine("Output:")
                    .AppendLine("```text")
                    .AppendLine(TrimToMaxLength(output, 1500))
                    .AppendLine("```");
            }
        }
        else
        {
            builder.AppendLine().AppendLine($"Reason: {exception.Message}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string TrimToMaxLength(string value, int maxLength) =>
        value.Length <= maxLength ? value.Trim() : $"{value[..maxLength].TrimEnd()}...";
}
