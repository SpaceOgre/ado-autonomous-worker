using System.Text.RegularExpressions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Core.Services;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ado.AutonomousWorker.Infrastructure.Services;

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

    public async Task<RepoExecutionContext> CreateRepoExecutionAsync(int parentWorkItemId, int childTaskId, RepoKind repoKind, string slug, CancellationToken cancellationToken)
    {
        var profile = GetProfile(repoKind);
        var safeSlug = UnsafeCharactersRegex().Replace(slug.ToLowerInvariant(), "-").Trim('-');
        var branchName = $"feature/{childTaskId}_{safeSlug}";
        var worktreePath = Path.Combine(profile.WorktreeRoot, $"{parentWorkItemId}-{childTaskId}-{repoKind.ToString().ToLowerInvariant()}");
        var artifactFolder = Path.Combine(storageOptions.Value.ArtifactRoot, parentWorkItemId.ToString(), childTaskId.ToString());
        Directory.CreateDirectory(artifactFolder);

        if (!Directory.Exists(profile.BaseClonePath))
        {
            throw new DirectoryNotFoundException($"Base clone path '{profile.BaseClonePath}' was not found for {repoKind}.");
        }

        if (!Directory.Exists(worktreePath))
        {
            await commandRunner.RunAsync(
                "git",
                ["-C", profile.BaseClonePath, "fetch", "origin", profile.MainBranch],
                profile.BaseClonePath,
                Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-fetch.log"),
                environmentVariables: null,
                cancellationToken);

            var addResult = await commandRunner.RunAsync(
                "git",
                ["-C", profile.BaseClonePath, "worktree", "add", worktreePath, $"origin/{profile.MainBranch}"],
                profile.BaseClonePath,
                Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-worktree-add.log"),
                environmentVariables: null,
                cancellationToken);

            if (!addResult.Success)
            {
                throw new InvalidOperationException($"Failed to create worktree for {repoKind}: {addResult.StandardError}");
            }
        }

        var checkoutResult = await commandRunner.RunAsync(
            "git",
            ["-C", worktreePath, "checkout", "-B", branchName],
            worktreePath,
            Path.Combine(artifactFolder, $"{repoKind.ToString().ToLowerInvariant()}-checkout.log"),
            environmentVariables: null,
            cancellationToken);

        if (!checkoutResult.Success)
        {
            throw new InvalidOperationException($"Failed to create branch '{branchName}': {checkoutResult.StandardError}");
        }

        logger.LogInformation("Prepared {RepoKind} worktree at {Path}", repoKind, worktreePath);
        return new RepoExecutionContext(parentWorkItemId, childTaskId, repoKind, branchName, worktreePath);
    }

    public async Task CleanupAsync(RepoExecutionContext context, CancellationToken cancellationToken)
    {
        var profile = GetProfile(context.RepoKind);
        if (!Directory.Exists(context.WorkingDirectory))
        {
            return;
        }

        var artifactFolder = Path.Combine(storageOptions.Value.ArtifactRoot, context.ParentWorkItemId.ToString(), context.ChildTaskId.ToString());
        await commandRunner.RunAsync(
            "git",
            ["-C", profile.BaseClonePath, "worktree", "remove", "--force", context.WorkingDirectory],
            profile.BaseClonePath,
            Path.Combine(artifactFolder, $"{context.RepoKind.ToString().ToLowerInvariant()}-cleanup.log"),
            environmentVariables: null,
            cancellationToken);
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
