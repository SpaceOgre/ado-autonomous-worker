namespace Ado.AutonomousWorker.Infrastructure.Execution;

internal static class CommandPathResolver
{
    public static bool Exists(string commandPath)
    {
        return Resolve(commandPath) is not null;
    }

    public static string ResolveOrOriginal(string commandPath)
    {
        return Resolve(commandPath) ?? commandPath;
    }

    private static string? Resolve(string commandPath)
    {
        if (string.IsNullOrWhiteSpace(commandPath))
        {
            return null;
        }

        if (Path.IsPathRooted(commandPath))
        {
            return File.Exists(commandPath) ? commandPath : null;
        }

        var searchFolders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var candidateNames = GetCandidateNames(commandPath);
        foreach (var folder in searchFolders)
        {
            foreach (var candidateName in candidateNames)
            {
                var candidatePath = Path.Combine(folder, candidateName);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetCandidateNames(string commandPath)
    {
        var names = new List<string> { commandPath };
        if (!Path.HasExtension(commandPath))
        {
            foreach (var extension in GetExecutableExtensions())
            {
                names.Add(commandPath + extension);
            }
        }

        return names;
    }

    private static IReadOnlyList<string> GetExecutableExtensions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            return [".exe", ".cmd", ".bat"];
        }

        return pathExt
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
