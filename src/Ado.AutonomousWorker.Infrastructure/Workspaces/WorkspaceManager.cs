using System.Text.RegularExpressions;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Workspaces;

// The base clone may be the developer's own checkout, so it is only ever fetched from and used to
// register worktrees: no pull, checkout or other change to its working tree.
public sealed partial class WorkspaceManager(
    IOptions<RepositoryOptions> repositoryOptions,
    IOptions<StorageOptions> storageOptions,
    ProcessCommandRunner commandRunner,
    ILogger<WorkspaceManager> logger
) : IWorkspaceManager
{
    private RepositoryOptions Repository => repositoryOptions.Value;

    public Task PrepareAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Repository.WorktreeRoot);
        Directory.CreateDirectory(storageOptions.Value.ArtifactRoot);
        return Task.CompletedTask;
    }

    public async Task<RepoExecutionContext> CreateWorkspaceAsync(
        int parentWorkItemId,
        int branchOwnerId,
        string slug,
        bool preserveExistingWorkspace,
        CancellationToken cancellationToken
    )
    {
        var safeSlug = UnsafeCharactersRegex().Replace(slug.ToLowerInvariant(), "-").Trim('-');
        var branchName = $"feature/{branchOwnerId}_{safeSlug}";
        var worktreePath = GetWorktreePath(branchName);
        var artifactFolder = storageOptions.Value.GetTaskArtifactRoot(parentWorkItemId, branchOwnerId);
        Directory.CreateDirectory(artifactFolder);
        EnsureBaseCloneExists();

        if (Directory.Exists(worktreePath) && preserveExistingWorkspace)
        {
            await EnsureExpectedBranchAsync(worktreePath, branchName, artifactFolder, cancellationToken);
            logger.LogInformation(
                "Reusing existing worktree at {Path} on branch {BranchName}.",
                worktreePath,
                branchName
            );
            return new RepoExecutionContext(parentWorkItemId, branchOwnerId, branchName, worktreePath);
        }

        await FetchAsync(Repository.MainBranch, artifactFolder, cancellationToken);

        await PrepareWorktreeAsync(
            worktreePath,
            $"origin/{Repository.MainBranch}",
            artifactFolder,
            refuseIfDirty: true,
            cancellationToken
        );

        var checkoutResult = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, "git-checkout.log"),
            ["checkout", "-B", branchName],
            cancellationToken
        );

        if (!checkoutResult.Success)
            throw new InvalidOperationException(
                $"Failed to create branch '{branchName}': {checkoutResult.StandardError}"
            );

        logger.LogInformation("Prepared worktree at {Path} on branch {BranchName}.", worktreePath, branchName);
        return new RepoExecutionContext(parentWorkItemId, branchOwnerId, branchName, worktreePath);
    }

    public async Task<RepoExecutionContext> CheckoutPrBranchAsync(
        int parentWorkItemId,
        int branchOwnerId,
        string branchName,
        bool preserveExistingWorkspace,
        CancellationToken cancellationToken
    )
    {
        var worktreePath = GetWorktreePath(branchName);
        var artifactFolder = storageOptions.Value.GetTaskArtifactRoot(parentWorkItemId, branchOwnerId);
        Directory.CreateDirectory(artifactFolder);
        EnsureBaseCloneExists();

        if (Directory.Exists(worktreePath) && preserveExistingWorkspace)
        {
            await EnsureExpectedBranchAsync(worktreePath, branchName, artifactFolder, cancellationToken);
            logger.LogInformation(
                "Reusing existing PR worktree at {Path} on branch {BranchName}.",
                worktreePath,
                branchName
            );
            return new RepoExecutionContext(parentWorkItemId, branchOwnerId, branchName, worktreePath);
        }

        await FetchAsync(branchName, artifactFolder, cancellationToken);

        // The worktree may be one an earlier run or an interactive session left for this branch.
        // Fast-forward it rather than recreating it, so local work is never thrown away.
        if (Directory.Exists(worktreePath))
        {
            await EnsureCleanAsync(worktreePath, artifactFolder, cancellationToken);
            await EnsureExpectedBranchAsync(worktreePath, branchName, artifactFolder, cancellationToken);

            var mergeResult = await RunGitAsync(
                worktreePath,
                Path.Combine(artifactFolder, "git-merge-ff.log"),
                ["merge", "--ff-only", $"origin/{branchName}"],
                cancellationToken
            );

            if (!mergeResult.Success)
                throw new InvalidOperationException(
                    $"Existing worktree '{worktreePath}' could not be fast-forwarded to origin/{branchName}: {mergeResult.StandardError}"
                );

            logger.LogInformation(
                "Reusing existing PR worktree at {Path} on branch {BranchName}.",
                worktreePath,
                branchName
            );
            return new RepoExecutionContext(parentWorkItemId, branchOwnerId, branchName, worktreePath);
        }

        await PrepareWorktreeAsync(
            worktreePath,
            $"origin/{branchName}",
            artifactFolder,
            refuseIfDirty: true,
            cancellationToken
        );

        var checkoutResult = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, "git-checkout.log"),
            ["checkout", "-B", branchName, $"origin/{branchName}"],
            cancellationToken
        );

        if (!checkoutResult.Success)
            throw new InvalidOperationException(
                $"Failed to create local tracking branch '{branchName}': {checkoutResult.StandardError}"
            );

        logger.LogInformation("Prepared PR worktree at {Path} on branch {BranchName}.", worktreePath, branchName);
        return new RepoExecutionContext(parentWorkItemId, branchOwnerId, branchName, worktreePath);
    }

    public async Task<string> PreparePlanningSnapshotAsync(
        string artifactDirectory,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(artifactDirectory);
        EnsureBaseCloneExists();

        var snapshotPath = Path.Combine(storageOptions.Value.DataRoot, "plan-snapshot");
        await FetchAsync(Repository.MainBranch, artifactDirectory, cancellationToken);
        await PrepareWorktreeAsync(
            snapshotPath,
            $"origin/{Repository.MainBranch}",
            artifactDirectory,
            refuseIfDirty: false,
            cancellationToken
        );

        logger.LogInformation(
            "Prepared planning snapshot of origin/{MainBranch} at {Path}.",
            Repository.MainBranch,
            snapshotPath
        );
        return snapshotPath;
    }

    public async Task<IReadOnlyList<RepoKind>> GetChangedRepoKindsAsync(
        RepoExecutionContext context,
        string artifactDirectory,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(artifactDirectory);

        var mergeBaseResult = await RunGitAsync(
            context.WorkingDirectory,
            Path.Combine(artifactDirectory, "git-merge-base.log"),
            ["merge-base", $"origin/{Repository.MainBranch}", "HEAD"],
            cancellationToken
        );

        if (!mergeBaseResult.Success)
            throw new InvalidOperationException(
                $"Failed to find the merge base with origin/{Repository.MainBranch}: {mergeBaseResult.StandardError}"
            );

        // Diffing the working tree (not HEAD) against the merge base covers committed and uncommitted changes.
        var diffResult = await RunGitAsync(
            context.WorkingDirectory,
            Path.Combine(artifactDirectory, "git-diff-name-only.log"),
            ["diff", "--name-only", mergeBaseResult.StandardOutput.Trim()],
            cancellationToken
        );
        var untrackedResult = await RunGitAsync(
            context.WorkingDirectory,
            Path.Combine(artifactDirectory, "git-ls-untracked.log"),
            ["ls-files", "--others", "--exclude-standard"],
            cancellationToken
        );

        if (!diffResult.Success || !untrackedResult.Success)
            throw new InvalidOperationException(
                $"Failed to list changed files in '{context.WorkingDirectory}': {diffResult.StandardError}{untrackedResult.StandardError}"
            );

        var changedPaths = $"{diffResult.StandardOutput}\n{untrackedResult.StandardOutput}".Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        return new[] { Repository.Backend, Repository.Frontend }
            .Where(profile =>
                !string.IsNullOrWhiteSpace(profile.Folder)
                && changedPaths.Any(path =>
                    path.StartsWith($"{profile.Folder.Trim('/', '\\')}/", StringComparison.OrdinalIgnoreCase)
                )
            )
            .Select(profile => profile.Kind)
            .ToArray();
    }

    public async Task CleanupAsync(RepoExecutionContext context, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(context.WorkingDirectory))
            return;

        var artifactFolder = storageOptions.Value.GetTaskArtifactRoot(context.ParentWorkItemId, context.BranchOwnerId);
        await RunGitAsync(
            Repository.BaseClonePath,
            Path.Combine(artifactFolder, "git-worktree-cleanup.log"),
            ["worktree", "remove", "--force", context.WorkingDirectory],
            cancellationToken
        );
    }

    private string GetWorktreePath(string branchName)
    {
        // feature/31011_slug → {WorktreeRoot}\31011_slug, the layout the interactive ADO skills use too.
        var separator = branchName.IndexOf('/');
        var folderName = (separator >= 0 ? branchName[(separator + 1)..] : branchName).Replace('/', '-');
        return Path.Combine(Repository.WorktreeRoot, folderName);
    }

    private void EnsureBaseCloneExists()
    {
        if (!Directory.Exists(Repository.BaseClonePath))
            throw new DirectoryNotFoundException($"Base clone path '{Repository.BaseClonePath}' was not found.");
    }

    private async Task FetchAsync(string branchName, string artifactFolder, CancellationToken cancellationToken)
    {
        var fetchResult = await RunGitAsync(
            Repository.BaseClonePath,
            Path.Combine(artifactFolder, "git-fetch.log"),
            ["fetch", "origin", branchName],
            cancellationToken
        );

        if (!fetchResult.Success)
            throw new InvalidOperationException($"Failed to fetch branch '{branchName}': {fetchResult.StandardError}");
    }

    // Prune stale registrations, force-remove the directory if it exists, then force-add.
    // Using -f means git won't reject the add if the branch is already checked out elsewhere.
    private async Task PrepareWorktreeAsync(
        string worktreePath,
        string remoteRef,
        string artifactFolder,
        bool refuseIfDirty,
        CancellationToken cancellationToken
    )
    {
        var baseClonePath = Repository.BaseClonePath;

        await RunGitAsync(
            baseClonePath,
            Path.Combine(artifactFolder, "git-worktree-prune.log"),
            ["worktree", "prune"],
            cancellationToken
        );

        if (Directory.Exists(worktreePath))
        {
            // The worktree root is shared with interactive sessions; never discard someone's uncommitted work.
            if (refuseIfDirty)
                await EnsureCleanAsync(worktreePath, artifactFolder, cancellationToken);

            logger.LogWarning("Removing existing worktree at '{Path}' before recreating.", worktreePath);
            await RunGitAsync(
                baseClonePath,
                Path.Combine(artifactFolder, "git-worktree-remove.log"),
                ["worktree", "remove", "--force", worktreePath],
                cancellationToken
            );
        }

        var addResult = await RunGitAsync(
            baseClonePath,
            Path.Combine(artifactFolder, "git-worktree-add.log"),
            ["worktree", "add", "-f", "--detach", worktreePath, remoteRef],
            cancellationToken
        );

        if (!addResult.Success)
            throw new InvalidOperationException(
                $"Failed to create worktree at '{worktreePath}': {addResult.StandardError}"
            );
    }

    private async Task EnsureCleanAsync(string worktreePath, string artifactFolder, CancellationToken cancellationToken)
    {
        var statusResult = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, "git-status-existing.log"),
            ["status", "--porcelain"],
            cancellationToken
        );

        // A folder git cannot read is not a worktree with someone's changes in it; let the caller replace it.
        if (statusResult.Success && !string.IsNullOrWhiteSpace(statusResult.StandardOutput))
            throw new InvalidOperationException(
                $"Existing worktree '{worktreePath}' has uncommitted changes. Commit or discard them, or resume the earlier execution, before running again."
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
        CancellationToken cancellationToken
    )
    {
        var showCurrentBranch = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, "git-branch-current.log"),
            ["branch", "--show-current"],
            cancellationToken
        );

        if (!showCurrentBranch.Success)
            throw new InvalidOperationException(
                $"Failed to inspect current branch of '{worktreePath}': {showCurrentBranch.StandardError}"
            );

        var currentBranch = showCurrentBranch.StandardOutput.Trim();
        if (string.Equals(currentBranch, branchName, StringComparison.OrdinalIgnoreCase))
            return;

        var checkoutResult = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, "git-checkout-existing.log"),
            ["checkout", branchName],
            cancellationToken
        );

        if (!checkoutResult.Success)
            throw new InvalidOperationException(
                $"Existing worktree '{worktreePath}' is on '{currentBranch}' and could not switch to '{branchName}': {checkoutResult.StandardError}"
            );
    }

    [GeneratedRegex("[^a-z0-9-]+", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeCharactersRegex();
}
