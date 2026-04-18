namespace Ado.AutonomousWorker.Cli.Interactive;

/// <summary>
/// Catalog of all slash commands available in interactive mode.
/// Provides search and lookup capabilities for tab completion.
/// </summary>
public sealed class SlashCommandRegistry
{
    private static readonly List<SlashCommandDescriptor> AllCommands =
    [
        // Interactive built-ins
        new("/help", "Show available commands and keyboard shortcuts", "Interactive", IsBuiltin: true),
        new("/clear", "Clear the terminal screen", "Interactive", IsBuiltin: true),
        new("/exit", "Exit interactive mode", "Interactive", IsBuiltin: true),
        new("/quit", "Exit interactive mode", "Interactive", IsBuiltin: true),
        // Queue & Run
        new("/queue", "List eligible AI-Work and AI-Rework items", "Queue"),
        new("/run", "Preview queue, then process and stop on first failure", "Queue"),
        // Execution
        new("/executions", "List local task executions", "Execution"),
        new("/execution show", "Show details of a specific execution", "Execution"),
        // Task management
        new("/task run", "Run a specific Azure DevOps work item or PR", "Task"),
        new("/task resume", "Resume a prior execution by ID", "Task"),
        new("/task set-phase", "Rewind an execution to an earlier phase", "Task"),
        // Diagnostics & Tools
        new("/doctor", "Verify config, repos, CLI tools, and ADO access", "Diagnostics"),
        new("/agent test", "Test connectivity to configured AI agents", "Diagnostics"),
        new("/version", "Show installed version and runtime info", "Diagnostics"),
        // Setup
        new("/bootstrap", "Initialize storage and workspace directories", "Setup"),
        new("/reset", "Delete the local worker database", "Setup"),
        new("/completion powershell", "Print PowerShell tab-completion script", "Setup"),
        // Deployment
        new("/deploy", "Trigger pipelines, wait for success, move items", "Deploy"),
        // UI
        new("/ui splash", "Render the run splash banner", "UI"),
        new("/ui run-preview", "Preview queue slice for processing", "UI"),
        // Azure DevOps
        new("/ado comment-test", "Post a test comment to a work item", "Azure DevOps"),
    ];

    /// <summary>Returns all registered slash commands.</summary>
    public IReadOnlyList<SlashCommandDescriptor> GetAll() => AllCommands;

    /// <summary>
    /// Searches for commands matching a prefix (with or without leading /).
    /// </summary>
    public IReadOnlyList<SlashCommandDescriptor> Search(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return AllCommands;

        var normalized = prefix.StartsWith('/') ? prefix : "/" + prefix;
        return AllCommands.Where(c => c.Command.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Finds the best matching command for a full or partial input.
    /// Prefers longer matches (e.g., "/task run" over "/task").
    /// </summary>
    public SlashCommandDescriptor? FindBestMatch(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var normalized = input.TrimStart('/');
        var withSlash = "/" + normalized;

        return AllCommands
            .Where(c =>
                withSlash.StartsWith(c.Command, StringComparison.OrdinalIgnoreCase)
                || c.Command.StartsWith(withSlash, StringComparison.OrdinalIgnoreCase)
            )
            .OrderByDescending(c =>
                c.Command.Equals(withSlash, StringComparison.OrdinalIgnoreCase) ? 2
                : withSlash.StartsWith(c.Command, StringComparison.OrdinalIgnoreCase) ? 1
                : 0
            )
            .ThenByDescending(c => c.Command.Length)
            .FirstOrDefault();
    }

    /// <summary>Returns distinct categories in registration order.</summary>
    public IReadOnlyList<string> GetCategories() => AllCommands.Select(c => c.Category).Distinct().ToList();

    /// <summary>Returns all commands in a specific category.</summary>
    public IReadOnlyList<SlashCommandDescriptor> GetByCategory(string category) =>
        AllCommands.Where(c => c.Category == category).ToList();
}
