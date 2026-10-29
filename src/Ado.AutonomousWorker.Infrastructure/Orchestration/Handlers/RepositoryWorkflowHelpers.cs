using System.Text.RegularExpressions;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration.Handlers;

internal static class RepositoryWorkflowHelpers
{
    public static string DetermineAgentWorkingDirectory(IEnumerable<string> workingDirectories)
    {
        var directories = workingDirectories
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (directories.Length == 0)
            throw new InvalidOperationException("At least one working directory is required.");

        if (directories.Length == 1)
            return directories[0];

        var commonPath = directories[0];
        foreach (var directory in directories.Skip(1))
        {
            commonPath = GetCommonPath(commonPath, directory);
            if (string.IsNullOrWhiteSpace(commonPath))
                return Directory.GetCurrentDirectory();
        }

        return commonPath;
    }

    public static string BuildRepositorySummary(
        IReadOnlyList<RepositoryExecution> repositoryExecutions,
        string agentWorkingDirectory
    )
    {
        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            repositoryExecutions.Select(re =>
            {
                var folderPath = re.FolderPath;
                var relativePath = Path.GetRelativePath(agentWorkingDirectory, folderPath);
                var validation =
                    re.Profile.ValidationCommands.Count == 0
                        ? "None"
                        : string.Join(" && ", re.Profile.ValidationCommands);
                var validationDirectory = string.IsNullOrWhiteSpace(re.Profile.ValidationWorkingDirectory)
                    ? "."
                    : re.Profile.ValidationWorkingDirectory;

                return $$"""
                    [{{re.RepoKind}}]
                    Child task: #{{re.ChildTask.WorkItemId}} {{re.ChildTask.Title}}
                    Branch: {{re.Workspace.BranchName}}
                    Relative path: {{relativePath}}
                    Full path: {{folderPath}}
                    Validation (run from {{validationDirectory}}): {{validation}}
                    """;
            })
        );
    }

    /// <summary>The single worktree shared by every half of an execution.</summary>
    public static RepoExecutionContext GetSharedWorkspace(IReadOnlyList<RepositoryExecution> repositoryExecutions) =>
        repositoryExecutions.Count > 0
            ? repositoryExecutions[0].Workspace
            : throw new InvalidOperationException("At least one repository execution is required.");

    /// <summary>
    /// The work item the branch is named after: the work item itself for a Bug or Task, otherwise the child
    /// task of the first half in scope (backend before frontend).
    /// </summary>
    public static ChildTaskRecord GetBranchOwner(IEnumerable<ChildTaskRecord> childTasks) =>
        childTasks.OrderBy(t => t.RepoKind).First();

    public static string FormatRepoKinds(IEnumerable<RepoKind> repoKinds) =>
        string.Join(" + ", repoKinds.Distinct().Select(k => k.ToString()));

    internal static IReadOnlyList<RepoKind> MapScopeToRepos(WorkItemScope scope) =>
        scope switch
        {
            WorkItemScope.Backend => [RepoKind.Backend],
            WorkItemScope.Frontend => [RepoKind.Frontend],
            _ => [RepoKind.Backend, RepoKind.Frontend],
        };

    internal static string ComputeSlug(string title)
    {
        var transliterated = title
            .ToLowerInvariant()
            .Replace("å", "a")
            .Replace("ä", "a")
            .Replace("ö", "o")
            .Replace("é", "e")
            .Replace("è", "e")
            .Replace("ü", "u")
            .Replace("ñ", "n")
            .Replace("ç", "c");
        return Regex.Replace(transliterated, "[^a-z0-9]+", "-").Trim('-');
    }

    internal static RepositoryProfile GetProfile(this RepositoryOptions options, RepoKind repoKind) =>
        repoKind switch
        {
            RepoKind.Backend => options.Backend,
            RepoKind.Frontend => options.Frontend,
            _ => throw new InvalidOperationException($"Unknown repo kind '{repoKind}'."),
        };

    /// <summary>
    /// Builds the halves in scope for a PR fix from the files the PR branch changes, including uncommitted
    /// work in the worktree. Falls back to both halves when the branch only touches files outside them.
    /// </summary>
    internal static async Task<IReadOnlyList<RepositoryExecution>> BuildPrFixRepositoryExecutionsAsync(
        IWorkspaceManager workspaceManager,
        RepositoryOptions options,
        WorkItemDetails workItem,
        RepoExecutionContext workspace,
        string executionArtifactRoot,
        CancellationToken cancellationToken
    )
    {
        var changedKinds = await workspaceManager.GetChangedRepoKindsAsync(
            workspace,
            Path.Combine(executionArtifactRoot, "changed-files"),
            cancellationToken
        );
        IReadOnlyList<RepoKind> repoKinds =
            changedKinds.Count > 0 ? changedKinds : [RepoKind.Backend, RepoKind.Frontend];

        return repoKinds
            .Select(repoKind =>
            {
                var childTask = workItem.WorkItemType is WorkItemType.Bug or WorkItemType.Task
                    ? new ChildTaskRecord(workItem.WorkItemId, repoKind, workItem.Title, workItem.State)
                    : workItem.ChildTasks.FirstOrDefault(t => t.RepoKind == repoKind)
                        ?? new ChildTaskRecord(workItem.WorkItemId, repoKind, workItem.Title, workItem.State);
                return new RepositoryExecution(
                    childTask,
                    options.GetProfile(repoKind),
                    workspace,
                    Path.Combine(executionArtifactRoot, repoKind.ToString().ToLowerInvariant())
                );
            })
            .ToArray();
    }

    public static string FormatWorkItemLabel(WorkItemType workItemType) =>
        workItemType switch
        {
            WorkItemType.UserStory => "User Story",
            WorkItemType.Bug => "Bug",
            WorkItemType.Task => "Task",
            _ => "Work Item",
        };

    private static string GetCommonPath(string left, string right)
    {
        var leftRoot = Path.GetPathRoot(left) ?? string.Empty;
        var rightRoot = Path.GetPathRoot(right) ?? string.Empty;
        if (!string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var leftSegments = left[leftRoot.Length..]
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            );
        var rightSegments = right[rightRoot.Length..]
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            );

        var shared = 0;
        while (
            shared < leftSegments.Length
            && shared < rightSegments.Length
            && string.Equals(leftSegments[shared], rightSegments[shared], StringComparison.OrdinalIgnoreCase)
        )
            shared++;

        var commonPath = leftRoot;
        for (var i = 0; i < shared; i++)
            commonPath = Path.Combine(commonPath, leftSegments[i]);

        return commonPath;
    }
}
