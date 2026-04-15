using Ado.AutonomousWorker.Infrastructure.Execution;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal static class GitPublishHelper
{
    public static async Task RunGitAsync(
        ProcessCommandRunner commandRunner,
        string workingDirectory, string artifactDirectory,
        string artifactName, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            "git", ["-C", workingDirectory, .. arguments],
            workingDirectory, Path.Combine(artifactDirectory, artifactName),
            environmentVariables: null, activityName: null, outputCallback: null, cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException(result.StandardError);
    }

    public static async Task RunGitAllowingNoChangesAsync(
        ProcessCommandRunner commandRunner,
        string workingDirectory, string artifactDirectory,
        string artifactName, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            "git", ["-C", workingDirectory, .. arguments],
            workingDirectory, Path.Combine(artifactDirectory, artifactName),
            environmentVariables: null, activityName: null, outputCallback: null, cancellationToken);

        if (!result.Success &&
            !result.StandardOutput.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) &&
            !result.StandardError.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(result.StandardError);
        }
    }

    public static async Task<string> GetCommitShaAsync(
        ProcessCommandRunner commandRunner,
        string workingDirectory, string artifactDirectory,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            "git", ["-C", workingDirectory, "rev-parse", "HEAD"],
            workingDirectory, Path.Combine(artifactDirectory, "git-rev-parse.log"),
            environmentVariables: null, activityName: null, outputCallback: null, cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException(result.StandardError);

        return result.StandardOutput.Trim();
    }
}
