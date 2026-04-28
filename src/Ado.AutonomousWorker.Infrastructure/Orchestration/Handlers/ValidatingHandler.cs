using System.Text.RegularExpressions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal sealed partial class ValidatingHandler(
    ProcessCommandRunner commandRunner,
    IOptions<WorkerOptions> workerOptions,
    ILogger<ValidatingHandler> logger
) : IPhaseHandler
{
    public TaskPhase Phase => TaskPhase.Validating;

    public bool Accepts(PhaseContext context) => true;

    public async Task<PhaseTransition> ExecuteAsync(PhaseContext context, CancellationToken cancellationToken)
    {
        var failures = new List<ValidationFailure>();
        var validationSessionId = $"validation-{context.ExecutionId}";
        var sequence = 0L;

        foreach (var repositoryExecution in context.RepositoryExecutions)
        {
            var repoKind = repositoryExecution.RepoKind;
            var workspace = repositoryExecution.Workspace;
            var artifactDirectory = repositoryExecution.ArtifactDirectory;

            await context.Journal.AppendEventAsync(
                context.ExecutionId,
                TaskPhase.Validating,
                $"Running validation for {repoKind}.",
                cancellationToken
            );

            foreach (var validationCommand in repositoryExecution.Profile.ValidationCommands)
            {
                logger.LogInformation(
                    "Execution {ExecutionId}: running validation for {RepoKind}: {Command}",
                    context.ExecutionId,
                    repoKind,
                    validationCommand
                );

                var artifactPath = Path.Combine(artifactDirectory, $"{NormalizeFileName(validationCommand)}.log");
                var validationWorkingDirectory = string.IsNullOrWhiteSpace(
                    repositoryExecution.Profile.ValidationWorkingDirectory
                )
                    ? workspace.WorkingDirectory
                    : Path.Combine(workspace.WorkingDirectory, repositoryExecution.Profile.ValidationWorkingDirectory);

                await context.Journal.AppendValidationOutputAsync(
                    context.ExecutionId,
                    validationSessionId,
                    validationWorkingDirectory,
                    sequence++,
                    $"▶ {repoKind}: {validationCommand}",
                    cancellationToken
                );

                var result = await commandRunner.RunPowerShellAsync(
                    validationCommand,
                    validationWorkingDirectory,
                    artifactPath,
                    cancellationToken
                );

                if (!result.Success)
                {
                    logger.LogWarning(
                        "Execution {ExecutionId}: validation failed for {RepoKind}: {Command}",
                        context.ExecutionId,
                        repoKind,
                        validationCommand
                    );

                    await context.Journal.AppendValidationOutputAsync(
                        context.ExecutionId,
                        validationSessionId,
                        validationWorkingDirectory,
                        sequence++,
                        $"✗ {repoKind}: {validationCommand}",
                        cancellationToken
                    );

                    failures.Add(
                        new ValidationFailure(
                            repoKind,
                            validationCommand,
                            artifactPath,
                            result.StandardOutput,
                            result.StandardError
                        )
                    );
                    break; // one failure per repo is enough to surface; move on to the next repo
                }

                logger.LogInformation(
                    "Execution {ExecutionId}: validation passed for {RepoKind}: {Command}",
                    context.ExecutionId,
                    repoKind,
                    validationCommand
                );

                await context.Journal.AppendValidationOutputAsync(
                    context.ExecutionId,
                    validationSessionId,
                    validationWorkingDirectory,
                    sequence++,
                    $"✓ {repoKind}: {validationCommand}",
                    cancellationToken
                );
            }
        }

        if (failures.Count == 0)
            return PhaseTransition.To(TaskPhase.Publishing);

        if (context.ValidationRetryCount < workerOptions.Value.MaxValidationRetries)
        {
            context.ValidationRetryCount++;
            context.ValidationFailures = failures;

            var failureSummary = string.Join(", ", failures.Select(f => $"{f.RepoKind}: {f.Command}"));
            await context.Journal.AppendEventAsync(
                context.ExecutionId,
                TaskPhase.Validating,
                $"Validation failed ({failureSummary}). Retrying (attempt {context.ValidationRetryCount}/{workerOptions.Value.MaxValidationRetries}).",
                cancellationToken
            );

            return PhaseTransition.To(TaskPhase.FixingValidation);
        }

        var first = failures[0];
        throw new ValidationFailureException(
            first.RepoKind,
            first.Command,
            first.ArtifactPath,
            first.StandardOutput,
            first.StandardError
        );
    }

    private static string NormalizeFileName(string value) =>
        NormalizeRegex().Replace(value.ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex NormalizeRegex();
}
