namespace Ado.AutonomousWorker.Core.Models;

public sealed record PullRequestInfo(int PullRequestId, string Title, Uri Url, string? ArtifactUri = null);
