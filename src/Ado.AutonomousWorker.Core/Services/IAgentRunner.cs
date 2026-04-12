using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Core.Services;

public interface IAgentRunner
{
    AgentProvider Provider { get; }

    Task<AgentSessionStartResult> StartSessionAsync(
        AgentRole role,
        AgentRoleConfig config,
        string prompt,
        string workingDirectory,
        string artifactDirectory,
        string? outputSchema,
        Func<AgentOutputChunk, CancellationToken, Task>? outputCallback,
        CancellationToken cancellationToken);

    Task<AgentRunResult> ResumeSessionAsync(
        AgentSessionHandle session,
        AgentRoleConfig config,
        string prompt,
        string artifactDirectory,
        Func<AgentOutputChunk, CancellationToken, Task>? outputCallback,
        CancellationToken cancellationToken);

    Task<ReviewResult> RunReviewAsync(
        AgentRoleConfig config,
        string prompt,
        string workingDirectory,
        string artifactDirectory,
        string? outputSchema,
        Func<AgentOutputChunk, CancellationToken, Task>? outputCallback,
        CancellationToken cancellationToken);
}
