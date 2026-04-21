using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Models;

public sealed record DeployRecord(
    string DeployId,
    DeployStatus Status,
    DeployPhase Phase,
    string Ref,
    bool IsTag,
    int? BackendPipelineId,
    int? BackendRunId,
    int? FrontendPipelineId,
    int? FrontendRunId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? FailureReason
);
