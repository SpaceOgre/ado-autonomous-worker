namespace Ado.AutonomousWorker.Cli.Completion;

public static class CliCommandCatalog
{
    public static readonly IReadOnlyList<string> TopLevelCommands =
    [
        "bootstrap",
        "completion",
        "doctor",
        "execution",
        "executions",
        "queue",
        "reset",
        "run",
        "task",
        "ui",
        "version",
    ];

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> BranchCommands =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["completion"] = ["powershell"],
            ["execution"] = ["show"],
            ["task"] = ["resume", "run"],
            ["ui"] = ["run-preview", "splash"],
        };

    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> CommandOptions =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["execution"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["show"] = ["--id", "--last"],
            },
            ["executions"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = ["--active"],
            },
            ["reset"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = ["--force"],
            },
            ["task"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["resume"] = ["--execution-id", "--last"],
                ["run"] = ["--id"],
            },
            ["ui"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["splash"] = ["--status"],
            },
        };
}
