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

    public async Task<RepoExecutionContext> CreateRepoExecutionAsync(int parentWorkItemId, int childTaskId, RepoKind repoKind, string slug, bool preserveExistingWorkspace, CancellationToken cancellationToken)
    {
        var profile = GetProfile(repoKind);
        var safeSlug = UnsafeCharactersRegex().Replace(slug.ToLowerInvariant(), "-").Trim('-');
        var branchName = $"feature/{childTaskId}_{safeSlug}";
        var worktreePath = Path.Combine(profile.WorktreeRoot, $"{parentWorkItemId}-{childTaskId}-{repoKind.ToString().ToLowerInvariant()}");
        var artifactFolder = storageOptions.Value.GetTaskArtifactRoot(parentWorkItemId, childTaskId);
        Directory.CreateDirectory(artifactFolder);

        if (!Directory.Exists(profile.BaseClonePath))
        {
            throw new DirectoryNotFoundException($"Base clone path '{profile.BaseClonePath}' was not found for {repoKind}.");
        }

        if (Directory.Exists(worktreePath) && preserveExistingWorkspace)
        {
            await EnsureExpectedBranchAsync(worktreePath, branchName, artifactFolder, repoKind, cancellationToken);
            logger.LogInformation(
                "Reusing existing {RepoKind} worktree at {Path} on branch {BranchName}.",
                repoKind,
                worktreePath,
                branchName);
            return new RepoExecutionContext(parentWorkItemId, childTaskId, repoKind, branchName, worktreePath);
        }

        if (!Directory.Exists(worktreePath))
        {
            var fetchResult = await RunGitAsync(
                profile.BaseClonePath,
                Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-pull.log"),
                ["pull", "origin", profile.MainBranch],
                cancellationToken);

            if (!fetchResult.Success)
            {
                throw new InvalidOperationException($"Failed to pull branch '{profile.MainBranch}' for {repoKind}: {fetchResult.StandardError}");
            }

            var addLogPath = Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-worktree-add.log");
            var addResult = await RunGitAsync(
                profile.BaseClonePath,
                addLogPath,
                ["worktree", "add", worktreePath, $"origin/{profile.MainBranch}"],
                cancellationToken);

            if (!addResult.Success)
            {
                logger.LogWarning(
                    "Worktree add failed for {RepoKind} at {Path}. Attempting prune before retry. Error: {Error}",
                    repoKind,
                    worktreePath,
                    addResult.StandardError.Trim());

                var pruneResult = await RunGitAsync(
                    profile.BaseClonePath,
                    Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-worktree-prune.log"),
                    ["worktree", "prune"],
                    cancellationToken);

                if (!pruneResult.Success)
                {
                    throw new InvalidOperationException($"Failed to prune worktrees for {repoKind}: {pruneResult.StandardError}");
                }

                addResult = await RunGitAsync(
                    profile.BaseClonePath,
                    addLogPath,
                    ["worktree", "add", "-f", worktreePath, $"origin/{profile.MainBranch}"],
                    cancellationToken);

                if (!addResult.Success)
                {
                    throw new InvalidOperationException($"Failed to create worktree for {repoKind}: {addResult.StandardError}");
                }
            }
        }

        var checkoutResult = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-checkout.log"),
            ["checkout", "-B", branchName],
            cancellationToken);

        if (!checkoutResult.Success)
        {
            throw new InvalidOperationException($"Failed to create branch '{branchName}': {checkoutResult.StandardError}");
        }

        logger.LogInformation(
            "Prepared {RepoKind} worktree at {Path} on branch {BranchName}.",
            repoKind,
            worktreePath,
            branchName);
        return new RepoExecutionContext(parentWorkItemId, childTaskId, repoKind, branchName, worktreePath);
    }

    public async Task CleanupAsync(RepoExecutionContext context, CancellationToken cancellationToken)
    {
        var profile = GetProfile(context.RepoKind);
        if (!Directory.Exists(context.WorkingDirectory))
        {
            return;
        }

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
        {
            throw new InvalidOperationException($"Failed to inspect current branch for {repoKind}: {showCurrentBranch.StandardError}");
        }

        var currentBranch = showCurrentBranch.StandardOutput.Trim();
        if (string.Equals(currentBranch, branchName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var checkoutResult = await RunGitAsync(
            worktreePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-checkout-existing.log"),
            ["checkout", branchName],
            cancellationToken);

        if (!checkoutResult.Success)
        {
            throw new InvalidOperationException(
                $"Existing worktree for {repoKind} is on '{currentBranch}' and could not switch to '{branchName}': {checkoutResult.StandardError}");
        }
    }

    private RepositoryProfile GetProfile(RepoKind kind) => kind switch
    {
        RepoKind.Backend => repositoryOptions.Value.Backend,
        RepoKind.Frontend => repositoryOptions.Value.Frontend,
        _ => throw new InvalidOperationException($"Unknown repo kind '{kind}'."),
    };

    [GeneratedRegex("[^a-z0-9-]+", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeCharactersRegex();
}
