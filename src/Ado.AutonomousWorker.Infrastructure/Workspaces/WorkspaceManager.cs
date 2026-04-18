using System.Text.RegularExpressions;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Workspaces;

public sealed partial class WorkspaceManager(
    IOptions<RepositoryOptions> repositoryOptions,
    IOptions<StorageOptions> storageOptions,
    ProcessCommandRunner commandRunner,
    ILogger<WorkspaceManager> logger
) : IWorkspaceManager
{
    public Task PrepareAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(repositoryOptions.Value.Backend.WorktreeRoot);
        Directory.CreateDirectory(repositoryOptions.Value.Frontend.WorktreeRoot);
        Directory.CreateDirectory(storageOptions.Value.ArtifactRoot);
        return Task.CompletedTask;
    }

    public async Task<RepoExecutionContext> CreateRepoExecutionAsync(
        int parentWorkItemId,
        int childTaskId,
        RepoKind repoKind,
        string slug,
        bool preserveExistingWorkspace,
        CancellationToken cancellationToken
    )
    {
        var profile = GetProfile(repoKind);
        var safeSlug = UnsafeCharactersRegex().Replace(slug.ToLowerInvariant(), "-").Trim('-');
        var branchName = $"feature/{childTaskId}_{safeSlug}";
        var worktreePath = Path.Combine(
            profile.WorktreeRoot,
            $"{childTaskId}-{repoKind.ToString().ToLowerInvariant()}"
        );
        var artifactFolder = storageOptions.Value.GetTaskArtifactRoot(parentWorkItemId, childTaskId);
        Directory.CreateDirectory(artifactFolder);

        if (!Directory.Exists(profile.BaseClonePath))
            throw new DirectoryNotFoundException(
                $"Base clone path '{profile.BaseClonePath}' was not found for {repoKind}."
            );

        if (Directory.Exists(worktreePath) && preserveExistingWorkspace)
        {
            await EnsureExpectedBranchAsync(worktreePath, branchName, artifactFolder, repoKind, cancellationToken);
            logger.LogInformation(
                "Reusing existing {RepoKind} worktree at {Path} on branch {BranchName}.",
                repoKind,
                worktreePath,
                branchName
            );
            return new RepoExecutionContext(parentWorkItemId, childTaskId, repoKind, branchName, worktreePath);
        }

        var pullResult = await RunGitAsync(
            profile.BaseClonePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-pull.log"),
            ["pull", "origin", profile.MainBranch],
            cancellationToken
        );

        if (!pullResult.Success)
            throw new InvalidOperationException(
                $"Failed to pull branch '{profile.MainBranch}' for {repoKind}: {pullResult.StandardError}"
            );

        await PrepareWorktreeAsync(
            profile.BaseClonePath,
            worktreePath,
            $"origin/{profile.MainBranch}",
            repoKind,
            artifactFolder,
            cancellationToken
        );

        var checkoutResult = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-checkout.log"),
            ["checkout", "-B", branchName],
            cancellationToken
        );

        if (!checkoutResult.Success)
            throw new InvalidOperationException(
                $"Failed to create branch '{branchName}': {checkoutResult.StandardError}"
            );

        logger.LogInformation(
            "Prepared {RepoKind} worktree at {Path} on branch {BranchName}.",
            repoKind,
            worktreePath,
            branchName
        );
        return new RepoExecutionContext(parentWorkItemId, childTaskId, repoKind, branchName, worktreePath);
    }

    public async Task<RepoExecutionContext> CheckoutPrBranchAsync(
        int parentWorkItemId,
        int childTaskId,
        RepoKind repoKind,
        string branchName,
        bool preserveExistingWorkspace,
        CancellationToken cancellationToken
    )
    {
        var profile = GetProfile(repoKind);
        var worktreePath = Path.Combine(
            profile.WorktreeRoot,
            $"{childTaskId}-{repoKind.ToString().ToLowerInvariant()}"
        );
        var artifactFolder = storageOptions.Value.GetTaskArtifactRoot(parentWorkItemId, childTaskId);
        Directory.CreateDirectory(artifactFolder);

        if (!Directory.Exists(profile.BaseClonePath))
            throw new DirectoryNotFoundException(
                $"Base clone path '{profile.BaseClonePath}' was not found for {repoKind}."
            );

        if (Directory.Exists(worktreePath) && preserveExistingWorkspace)
        {
            await EnsureExpectedBranchAsync(worktreePath, branchName, artifactFolder, repoKind, cancellationToken);
            logger.LogInformation(
                "Reusing existing {RepoKind} PR worktree at {Path} on branch {BranchName}.",
                repoKind,
                worktreePath,
                branchName
            );
            return new RepoExecutionContext(parentWorkItemId, childTaskId, repoKind, branchName, worktreePath);
        }

        var fetchResult = await RunGitAsync(
            profile.BaseClonePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-fetch.log"),
            ["fetch", "origin", branchName],
            cancellationToken
        );

        if (!fetchResult.Success)
            throw new InvalidOperationException(
                $"Failed to fetch branch '{branchName}' for {repoKind}: {fetchResult.StandardError}"
            );

        await PrepareWorktreeAsync(
            profile.BaseClonePath,
            worktreePath,
            $"origin/{branchName}",
            repoKind,
            artifactFolder,
            cancellationToken
        );

        var checkoutResult = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-checkout.log"),
            ["checkout", "-B", branchName, $"origin/{branchName}"],
            cancellationToken
        );

        if (!checkoutResult.Success)
            throw new InvalidOperationException(
                $"Failed to create local tracking branch '{branchName}': {checkoutResult.StandardError}"
            );

        logger.LogInformation(
            "Prepared {RepoKind} PR worktree at {Path} on branch {BranchName}.",
            repoKind,
            worktreePath,
            branchName
        );
        return new RepoExecutionContext(parentWorkItemId, childTaskId, repoKind, branchName, worktreePath);
    }

    public async Task PullAllBaseClonesAsync(string artifactDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(artifactDirectory);
        foreach (var profile in new[] { repositoryOptions.Value.Backend, repositoryOptions.Value.Frontend })
        {
            if (!Directory.Exists(profile.BaseClonePath))
            {
                logger.LogWarning(
                    "Skipping pull for {RepoKind}: base clone path '{Path}' not found.",
                    profile.Kind,
                    profile.BaseClonePath
                );
                continue;
            }

            var pullResult = await RunGitAsync(
                profile.BaseClonePath,
                Path.Combine(artifactDirectory, $"{profile.Kind.ToString().ToLowerInvariant()}-pull.log"),
                ["pull", "origin", profile.MainBranch],
                cancellationToken
            );

            if (!pullResult.Success)
                throw new InvalidOperationException(
                    $"Failed to pull '{profile.MainBranch}' for {profile.Kind}: {pullResult.StandardError}"
                );

            logger.LogInformation("Pulled {MainBranch} for {RepoKind}.", profile.MainBranch, profile.Kind);
        }
    }

    public async Task CleanupAsync(RepoExecutionContext context, CancellationToken cancellationToken)
    {
        var profile = GetProfile(context.RepoKind);
        if (!Directory.Exists(context.WorkingDirectory))
            return;

        var artifactFolder = storageOptions.Value.GetTaskArtifactRoot(context.ParentWorkItemId, context.ChildTaskId);
        await commandRunner.RunAsync(
            "git",
            ["-C", profile.BaseClonePath, "worktree", "remove", "--force", context.WorkingDirectory],
            profile.BaseClonePath,
            Path.Combine(artifactFolder, $"{context.RepoKind.ToString().ToLowerInvariant()}-cleanup.log"),
            environmentVariables: null,
            activityName: null,
            outputCallback: null,
            cancellationToken
        );
    }

    // Prune stale registrations, force-remove the directory if it exists, then force-add.
    // Using -f means git won't reject the add if the branch is already checked out elsewhere.
    private async Task PrepareWorktreeAsync(
        string baseClonePath,
        string worktreePath,
        string remoteRef,
        RepoKind repoKind,
        string artifactFolder,
        CancellationToken cancellationToken
    )
    {
        var logPrefix = repoKind.ToString().ToLowerInvariant();

        await RunGitAsync(
            baseClonePath,
            Path.Combine(artifactFolder, $"{logPrefix}-worktree-prune.log"),
            ["worktree", "prune"],
            cancellationToken
        );

        if (Directory.Exists(worktreePath))
        {
            logger.LogWarning(
                "Removing existing {RepoKind} worktree at '{Path}' before recreating.",
                repoKind,
                worktreePath
            );
            await RunGitAsync(
                baseClonePath,
                Path.Combine(artifactFolder, $"{logPrefix}-worktree-remove.log"),
                ["worktree", "remove", "--force", worktreePath],
                cancellationToken
            );
        }

        var addResult = await RunGitAsync(
            baseClonePath,
            Path.Combine(artifactFolder, $"{logPrefix}-worktree-add.log"),
            ["worktree", "add", "-f", worktreePath, remoteRef],
            cancellationToken
        );

        if (!addResult.Success)
            throw new InvalidOperationException(
                $"Failed to create worktree for {repoKind} at '{worktreePath}': {addResult.StandardError}"
            );
    }

    private Task<CommandResult> RunGitAsync(
        string workingDirectory,
        string artifactPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    )
    {
        return commandRunner.RunAsync(
            "git",
            ["-C", workingDirectory, .. arguments],
            workingDirectory,
            artifactPath,
            environmentVariables: null,
            activityName: null,
            outputCallback: null,
            cancellationToken
        );
    }

    private async Task EnsureExpectedBranchAsync(
        string worktreePath,
        string branchName,
        string artifactFolder,
        RepoKind repoKind,
        CancellationToken cancellationToken
    )
    {
        var showCurrentBranch = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-branch-current.log"),
            ["branch", "--show-current"],
            cancellationToken
        );

        if (!showCurrentBranch.Success)
            throw new InvalidOperationException(
                $"Failed to inspect current branch for {repoKind}: {showCurrentBranch.StandardError}"
            );

        var currentBranch = showCurrentBranch.StandardOutput.Trim();
        if (string.Equals(currentBranch, branchName, StringComparison.OrdinalIgnoreCase))
            return;

        var checkoutResult = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-checkout-existing.log"),
            ["checkout", branchName],
            cancellationToken
        );

        if (!checkoutResult.Success)
            throw new InvalidOperationException(
                $"Existing worktree for {repoKind} is on '{currentBranch}' and could not switch to '{branchName}': {checkoutResult.StandardError}"
            );
    }

    private RepositoryProfile GetProfile(RepoKind kind) =>
        kind switch
        {
            RepoKind.Backend => repositoryOptions.Value.Backend,
            RepoKind.Frontend => repositoryOptions.Value.Frontend,
            _ => throw new InvalidOperationException($"Unknown repo kind '{kind}'."),
        };

    [GeneratedRegex("[^a-z0-9-]+", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeCharactersRegex();
}
