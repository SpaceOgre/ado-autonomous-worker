using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record RepositoryPublishResult(
    RepoKind RepoKind,
    ChildTaskRecord ChildTask,
    string BranchName,
    string CommitSha,
    IReadOnlyList<string> ValidationCommands,
    PullRequestInfo PullRequest
);
