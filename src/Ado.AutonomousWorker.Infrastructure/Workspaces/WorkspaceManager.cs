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
    ILogger<WorkspaceManager> logger) : IWorkspaceManager
{
    public Task PrepareAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(repositoryOptions.Value.Backend.WorktreeRoot);
        Directory.CreateDirectory(repositoryOptions.Value.Frontend.WorktreeRoot);
        Directory.CreateDirectory(storageOptions.Value.ArtifactRoot);
        return Task.CompletedTask;
    }

    public async Task<RepoExecutionContext> CreateRepoExecutionAsync(
        int parentWorkItemId, int childTaskId, RepoKind repoKind,
        string slug, bool preserveExistingWorkspace, CancellationToken cancellationToken)
    {
        var profile = GetProfile(repoKind);
        var safeSlug = UnsafeCharactersRegex().Replace(slug.ToLowerInvariant(), "-").Trim('-');
        var branchName = $"feature/{childTaskId}_{safeSlug}";
        var worktreePath = Path.Combine(profile.WorktreeRoot, $"{childTaskId}-{repoKind.ToString().ToLowerInvariant()}");
        var artifactFolder = storageOptions.Value.GetTaskArtifactRoot(parentWorkItemId, childTaskId);
        Directory.CreateDirectory(artifactFolder);

        if (!Directory.Exists(profile.BaseClonePath))
            throw new DirectoryNotFoundException($"Base clone path '{profile.BaseClonePath}' was not found for {repoKind}.");

        if (Directory.Exists(worktreePath) && preserveExistingWorkspace)
        {
            await EnsureExpectedBranchAsync(worktreePath, branchName, artifactFolder, repoKind, cancellationToken);
            logger.LogInformation(
                "Reusing existing {RepoKind} worktree at {Path} on branch {BranchName}.",
                repoKind, worktreePath, branchName);
            return new RepoExecutionContext(parentWorkItemId, childTaskId, repoKind, branchName, worktreePath);
        }

        var pullResult = await RunGitAsync(
            profile.BaseClonePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-pull.log"),
            ["pull", "origin", profile.MainBranch],
            cancellationToken);

        if (!pullResult.Success)
            throw new InvalidOperationException($"Failed to pull branch '{profile.MainBranch}' for {repoKind}: {pullResult.StandardError}");

        await RemoveStaleWorktreeAsync(profile.BaseClonePath, worktreePath, repoKind, artifactFolder, cancellationToken);
        await AddWorktreeWithRetryAsync(profile.BaseClonePath, worktreePath, $"origin/{profile.MainBranch}", repoKind, artifactFolder, cancellationToken);

        var checkoutResult = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-checkout.log"),
            ["checkout", "-B", branchName],
            cancellationToken);

        if (!checkoutResult.Success)
            throw new InvalidOperationException($"Failed to create branch '{branchName}': {checkoutResult.StandardError}");

        logger.LogInformation(
            "Prepared {RepoKind} worktree at {Path} on branch {BranchName}.",
            repoKind, worktreePath, branchName);
        return new RepoExecutionContext(parentWorkItemId, childTaskId, repoKind, branchName, worktreePath);
    }

    public async Task<RepoExecutionContext> CheckoutPrBranchAsync(
        int parentWorkItemId, int childTaskId, RepoKind repoKind,
        string branchName, bool preserveExistingWorkspace, CancellationToken cancellationToken)
    {
        var profile = GetProfile(repoKind);
        var worktreePath = Path.Combine(profile.WorktreeRoot, $"{childTaskId}-{repoKind.ToString().ToLowerInvariant()}");
        var artifactFolder = storageOptions.Value.GetTaskArtifactRoot(parentWorkItemId, childTaskId);
        Directory.CreateDirectory(artifactFolder);

        if (!Directory.Exists(profile.BaseClonePath))
            throw new DirectoryNotFoundException($"Base clone path '{profile.BaseClonePath}' was not found for {repoKind}.");

        if (Directory.Exists(worktreePath) && preserveExistingWorkspace)
        {
            await EnsureExpectedBranchAsync(worktreePath, branchName, artifactFolder, repoKind, cancellationToken);
            logger.LogInformation(
                "Reusing existing {RepoKind} PR worktree at {Path} on branch {BranchName}.",
                repoKind, worktreePath, branchName);
            return new RepoExecutionContext(parentWorkItemId, childTaskId, repoKind, branchName, worktreePath);
        }

        var fetchResult = await RunGitAsync(
            profile.BaseClonePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-fetch.log"),
            ["fetch", "origin", branchName],
            cancellationToken);

        if (!fetchResult.Success)
            throw new InvalidOperationException($"Failed to fetch branch '{branchName}' for {repoKind}: {fetchResult.StandardError}");

        await RemoveStaleWorktreeAsync(profile.BaseClonePath, worktreePath, repoKind, artifactFolder, cancellationToken);
        await AddWorktreeWithRetryAsync(profile.BaseClonePath, worktreePath, $"origin/{branchName}", repoKind, artifactFolder, cancellationToken);

        // The worktree was created pointing to origin/{branchName} (detached). Create a local tracking branch.
        var checkoutLogPath = Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-checkout.log");
        var checkoutResult = await RunGitAsync(
            worktreePath,
            checkoutLogPath,
            ["checkout", "-B", branchName, $"origin/{branchName}"],
            cancellationToken);

        if (!checkoutResult.Success)
        {
            // If another worktree already holds the branch, reuse it — it contains the agent's
            // prior work and is exactly what the PR fix needs. Discard the detached worktree.
            var conflictPath = ExtractConflictingWorktreePath(checkoutResult.StandardError);
            if (conflictPath is not null)
            {
                logger.LogInformation(
                    "Branch '{BranchName}' is already checked out in existing worktree at '{ConflictPath}'. " +
                    "Reusing it and discarding the newly-created detached worktree.",
                    branchName, conflictPath);

                await RunGitAsync(
                    profile.BaseClonePath,
                    Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-worktree-remove-detached.log"),
                    ["worktree", "remove", "--force", worktreePath],
                    cancellationToken);

                // Bring the existing worktree up to the latest fetched remote state.
                var resetResult = await RunGitAsync(
                    conflictPath,
                    Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-reset.log"),
                    ["reset", "--hard", $"origin/{branchName}"],
                    cancellationToken);

                if (!resetResult.Success)
                {
                    throw new InvalidOperationException(
                        $"Failed to reset existing worktree '{conflictPath}' to origin/{branchName}: {resetResult.StandardError}");
                }


                logger.LogInformation(
                    "Reusing existing {RepoKind} PR worktree at {Path} on branch {BranchName}.",
                    repoKind, conflictPath, branchName);
                return new RepoExecutionContext(parentWorkItemId, childTaskId, repoKind, branchName, conflictPath);
            }

            throw new InvalidOperationException(
                $"Failed to create local tracking branch '{branchName}': {checkoutResult.StandardError}");
        }

        logger.LogInformation(
            "Prepared {RepoKind} PR worktree at {Path} on branch {BranchName}.",
            repoKind, worktreePath, branchName);
        return new RepoExecutionContext(parentWorkItemId, childTaskId, repoKind, branchName, worktreePath);
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
            cancellationToken);
    }

    private async Task RemoveStaleWorktreeAsync(
        string baseClonePath, string worktreePath, RepoKind repoKind,
        string artifactFolder, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktreePath))
            return;

        var logPrefix = repoKind.ToString().ToLowerInvariant();
        logger.LogWarning("Removing stale {RepoKind} worktree at '{Path}' before recreating.", repoKind, worktreePath);
        await RunGitAsync(
            baseClonePath,
            Path.Combine(artifactFolder, $"{logPrefix}-worktree-remove-stale.log"),
            ["worktree", "remove", "--force", worktreePath],
            cancellationToken);
        await RunGitAsync(
            baseClonePath,
            Path.Combine(artifactFolder, $"{logPrefix}-worktree-prune-stale.log"),
            ["worktree", "prune"],
            cancellationToken);
    }

    private async Task AddWorktreeWithRetryAsync(
        string baseClonePath, string worktreePath, string remoteRef, RepoKind repoKind,
        string artifactFolder, CancellationToken cancellationToken)
    {
        var logPrefix = repoKind.ToString().ToLowerInvariant();
        var addLogPath = Path.Combine(artifactFolder, $"{logPrefix}-worktree-add.log");

        var addResult = await RunGitAsync(
            baseClonePath, addLogPath,
            ["worktree", "add", worktreePath, remoteRef],
            cancellationToken);

        if (addResult.Success)
            return;

        logger.LogWarning(
            "Worktree add failed for {RepoKind} at {Path}. Attempting prune before retry. Error: {Error}",
            repoKind, worktreePath, addResult.StandardError.Trim());

        var pruneResult = await RunGitAsync(
            baseClonePath,
            Path.Combine(artifactFolder, $"{logPrefix}-worktree-prune.log"),
            ["worktree", "prune"],
            cancellationToken);

        if (!pruneResult.Success)
            throw new InvalidOperationException($"Failed to prune worktrees for {repoKind}: {pruneResult.StandardError}");

        addResult = await RunGitAsync(
            baseClonePath, addLogPath,
            ["worktree", "add", "-f", worktreePath, remoteRef],
            cancellationToken);

        if (!addResult.Success)
            throw new InvalidOperationException($"Failed to create worktree for {repoKind} at '{worktreePath}': {addResult.StandardError}");
    }

    private Task<CommandResult> RunGitAsync(
        string workingDirectory,
        string artifactPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return commandRunner.RunAsync(
            "git",
            ["-C", workingDirectory, .. arguments],
            workingDirectory,
            artifactPath,
            environmentVariables: null,
            activityName: null,
            outputCallback: null,
            cancellationToken);
    }

    private async Task EnsureExpectedBranchAsync(
        string worktreePath,
        string branchName,
        string artifactFolder,
        RepoKind repoKind,
        CancellationToken cancellationToken)
    {
        var showCurrentBranch = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-branch-current.log"),
            ["branch", "--show-current"],
            cancellationToken);

        if (!showCurrentBranch.Success)
            throw new InvalidOperationException($"Failed to inspect current branch for {repoKind}: {showCurrentBranch.StandardError}");

        var currentBranch = showCurrentBranch.StandardOutput.Trim();
        if (string.Equals(currentBranch, branchName, StringComparison.OrdinalIgnoreCase))
            return;

        var checkoutResult = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-checkout-existing.log"),
            ["checkout", branchName],
            cancellationToken);

        if (!checkoutResult.Success)
            throw new InvalidOperationException(
                $"Existing worktree for {repoKind} is on '{currentBranch}' and could not switch to '{branchName}': {checkoutResult.StandardError}");
    }

    private RepositoryProfile GetProfile(RepoKind kind) => kind switch
    {
        RepoKind.Backend => repositoryOptions.Value.Backend,
        RepoKind.Frontend => repositoryOptions.Value.Frontend,
        _ => throw new InvalidOperationException($"Unknown repo kind '{kind}'."),
    };

    // Parses "fatal: 'branchName' is already used by worktree at 'path'" from git stderr.
    private static string? ExtractConflictingWorktreePath(string stderr)
    {
        const string marker = "is already used by worktree at '";
        var idx = stderr.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;
        var start = idx + marker.Length;
        var end = stderr.IndexOf('\'', start);
        return end > start ? stderr[start..end] : null;
    }

    [GeneratedRegex("[^a-z0-9-]+", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeCharactersRegex();
}
