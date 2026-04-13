using Ado.AutonomousWorker.Core.Enums;

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

    public static string BuildRepositorySummary(IReadOnlyList<RepositoryExecution> repositoryExecutions, string agentWorkingDirectory)
    {
        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            repositoryExecutions.Select(re =>
            {
                var workspacePath = re.Workspace.WorkingDirectory;
                var relativePath = Path.GetRelativePath(agentWorkingDirectory, workspacePath);
                var validation = re.Profile.ValidationCommands.Count == 0
                    ? "None"
                    : string.Join(" | ", re.Profile.ValidationCommands);

                return $$"""
                [{{re.RepoKind}}]
                Child task: #{{re.ChildTask.WorkItemId}} {{re.ChildTask.Title}}
                Branch: {{re.Workspace.BranchName}}
                Relative path: {{relativePath}}
                Full path: {{workspacePath}}
                Validation: {{validation}}
                """;
            }));
    }

    public static string FormatRepoKinds(IEnumerable<RepoKind> repoKinds)
        => string.Join(" + ", repoKinds.Distinct().Select(k => k.ToString()));

    public static string FormatWorkItemLabel(WorkItemType workItemType) => workItemType switch
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
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var rightSegments = right[rightRoot.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var shared = 0;
        while (shared < leftSegments.Length && shared < rightSegments.Length &&
               string.Equals(leftSegments[shared], rightSegments[shared], StringComparison.OrdinalIgnoreCase))
            shared++;

        var commonPath = leftRoot;
        for (var i = 0; i < shared; i++)
            commonPath = Path.Combine(commonPath, leftSegments[i]);

        return commonPath;
    }
}
