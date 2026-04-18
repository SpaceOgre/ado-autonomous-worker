namespace Ado.AutonomousWorker.Core.Models;

public sealed record PrReviewThread(
    int ThreadId,
    string? FilePath,
    int? LineNumber,
    string Status,
    IReadOnlyList<PrReviewComment> Comments
);

public sealed record PrReviewComment(int CommentId, string Author, string Content);
