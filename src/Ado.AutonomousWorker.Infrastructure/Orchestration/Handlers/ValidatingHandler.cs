using System.Text.RegularExpressions;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed partial class ValidatingHandler(
    ProcessCommandRunner commandRunner,
    ILogger<ValidatingHandler> logger) : IPhaseHandler
{
    public TaskPhase Phase => TaskPhase.Validating;

    public async Task<TaskPhase> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken)
    {
        foreach (var repositoryExecution in context.RepositoryExecutions)
        {
            var repoKind = repositoryExecution.RepoKind;
            var workspace = repositoryExecution.Workspace;
            var artifactDirectory = repositoryExecution.ArtifactDirectory;

            await context.Journal.AppendEventAsync(
                context.ExecutionId, TaskPhase.Validating, $"Running validation for {repoKind}.", cancellationToken);

            foreach (var validationCommand in repositoryExecution.Profile.ValidationCommands)
            {
                logger.LogInformation(
                    "Execution {ExecutionId}: running validation for {RepoKind}: {Command}",
                    context.ExecutionId, repoKind, validationCommand);

                var artifactPath = Path.Combine(artifactDirectory, $"{NormalizeFileName(validationCommand)}.log");
                var result = await commandRunner.RunPowerShellAsync(
                    validationCommand, workspace.WorkingDirectory, artifactPath, cancellationToken);

                if (!result.Success)
                    throw new ValidationFailureException(
                        repoKind, validationCommand, artifactPath,
                        result.StandardOutput, result.StandardError);

                logger.LogInformation(
                    "Execution {ExecutionId}: validation passed for {RepoKind}: {Command}",
                    context.ExecutionId, repoKind, validationCommand);
            }
        }

        return TaskPhase.Publishing;
    }

    private static string NormalizeFileName(string value)
        => NormalizeRegex().Replace(value.ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex NormalizeRegex();
}
