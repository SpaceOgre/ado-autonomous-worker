using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

internal sealed record ValidationFailure(
    RepoKind RepoKind,
    string Command,
    string ArtifactPath,
    string StandardOutput,
    string StandardError
);
