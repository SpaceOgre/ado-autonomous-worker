using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal sealed class ValidationFailureException(
    RepoKind repoKind,
    string command,
    string artifactPath,
    string standardOutput,
    string standardError
) : InvalidOperationException($"Validation failed for {repoKind}: {command}")
{
    public RepoKind RepoKind { get; } = repoKind;

    public string Command { get; } = command;

    public string ArtifactPath { get; } = artifactPath;

    public string StandardOutput { get; } = standardOutput;

    public string StandardError { get; } = standardError;
}
