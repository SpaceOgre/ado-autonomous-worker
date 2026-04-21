namespace Ado.AutonomousWorker.Cli.Completion;

public static class CliCommandCatalog
{
    public static readonly IReadOnlyList<string> TopLevelCommands =
    [
        "ado",
        "agent",
        "bootstrap",
        "completion",
        "deploy",
        "dashboard",
        "doctor",
        "execution",
        "executions",
        "interactive",
        "queue",
        "reset",
        "run",
        "task",
        "ui",
        "version",
    ];

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> BranchCommands = new Dictionary<
        string,
        IReadOnlyList<string>
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["ado"] = ["comment-test"],
        ["agent"] = ["test"],
        ["completion"] = ["powershell"],
        ["execution"] = ["show"],
        ["task"] = ["resume", "run", "set-phase"],
        ["ui"] = ["run-preview", "splash"],
    };

    public static readonly IReadOnlyDictionary<
        string,
        IReadOnlyDictionary<string, IReadOnlyList<string>>
    > CommandOptions = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["agent"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["test"] = ["--role"],
        },
        ["deploy"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = ["--dry-run", "--tag", "--backend", "--frontend"],
        },
        ["dashboard"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = ["--port", "--no-open"],
        },
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
        ["run"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = ["--iteration", "--pr-fix-only"],
        },
        ["task"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["resume"] = ["--execution-id", "--last"],
            ["run"] = ["--id", "--pr", "--iteration"],
            ["set-phase"] = ["--execution-id", "--last", "--phase"],
        },
        ["ui"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["splash"] = ["--status"],
        },
    };
}
