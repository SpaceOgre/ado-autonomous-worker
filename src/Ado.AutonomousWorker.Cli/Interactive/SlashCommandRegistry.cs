namespace Ado.AutonomousWorker.Cli.Interactive;

/// <summary>
/// Catalog of all slash commands available in interactive mode.
/// Provides context-aware completions (commands → subcommands → flags).
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

    private record FlagDef(string Flag, string Description, bool TakesValue = false);

    private static readonly Dictionary<string, IReadOnlyList<FlagDef>> CommandFlagMap = new()
    {
        ["/run"] =
        [
            new("--iteration", "Override iteration path (e.g. 'Project\\Sprint 5')", TakesValue: true),
            new("--pr-fix-only", "Only process PR-fix items, skipping backlog items"),
        ],
        ["/deploy"] =
        [
            new("--dry-run", "Preview without running pipelines"),
            new("--tag", "Trigger from a specific git tag", TakesValue: true),
            new("--backend", "Only deploy the backend"),
            new("--frontend", "Only deploy the frontend"),
        ],
        ["/task run"] =
        [
            new("--id", "Azure DevOps work item ID", TakesValue: true),
            new("--pr", "Pull request ID to fix", TakesValue: true),
            new("--iteration", "Override iteration path", TakesValue: true),
        ],
        ["/task resume"] =
        [
            new("--execution-id", "Execution ID to resume", TakesValue: true),
            new("--last", "Resume the most recent execution"),
        ],
        ["/task set-phase"] =
        [
            new("--execution-id", "Execution ID to update", TakesValue: true),
            new("--last", "Update the most recent execution"),
            new("--phase", "Phase: Reviewing, ApplyingReview, Validating, Publishing", TakesValue: true),
        ],
        ["/agent test"] = [new("--role", "Role to test: plan, code, or review", TakesValue: true)],
        ["/executions"] = [new("--active", "Show only in-progress executions")],
        ["/execution show"] =
        [
            new("--id", "Execution ID to show", TakesValue: true),
            new("--last", "Show the most recent execution"),
        ],
        ["/ado comment-test"] = [new("--work-item", "Work item ID to post comment on", TakesValue: true)],
        ["/ui splash"] = [new("--status", "Optional status text under the banner", TakesValue: true)],
    };

    /// <summary>Returns all registered slash commands.</summary>
    public IReadOnlyList<SlashCommandDescriptor> GetAll() => AllCommands.AsReadOnly();

    /// <summary>
    /// Returns context-aware completions for the current buffer.
    /// Shows commands when typing a command prefix, subcommands after a parent command,
    /// and flags once a complete command has been entered.
    /// </summary>
    public IReadOnlyList<CompletionItem> GetCompletions(string buffer)
    {
        if (buffer.Length == 0)
            return [];

        var trimmed = buffer.TrimEnd();
        var endsWithSpace = buffer.Length > trimmed.Length;
        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
            return [];

        // Find where flags begin in the token list
        var firstFlagIdx = Array.FindIndex(tokens, t => t.StartsWith('-'));

        // No flags and no trailing space → user is typing a command prefix
        if (firstFlagIdx < 0 && !endsWithSpace)
        {
            var prefix = buffer.StartsWith('/') ? buffer : "/" + buffer;
            return AllCommands
                .Where(c => c.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(ToCommandItem)
                .ToList();
        }

        // Determine the command portion (tokens that precede the first flag)
        var commandTokens = firstFlagIdx >= 0 ? tokens[..firstFlagIdx] : tokens;
        var commandPart = string.Join(' ', commandTokens);
        var normalizedCmd = commandPart.StartsWith('/') ? commandPart : "/" + commandPart;

        // Look for an exact command match
        var exactCmd = AllCommands.FirstOrDefault(c =>
            c.Command.Equals(normalizedCmd, StringComparison.OrdinalIgnoreCase)
        );

        if (exactCmd == null)
        {
            // No exact match: if there's a trailing space and no flags yet, search subcommands
            if (endsWithSpace && firstFlagIdx < 0)
            {
                var subcmdPrefix = normalizedCmd + " ";
                return AllCommands
                    .Where(c => c.Command.StartsWith(subcmdPrefix, StringComparison.OrdinalIgnoreCase))
                    .Select(ToCommandItem)
                    .ToList();
            }

            return [];
        }

        // Exact command found — show its flags
        if (!CommandFlagMap.TryGetValue(exactCmd.Command, out var allFlags))
            return [];

        // Collect already-used flags (so they are excluded from suggestions)
        var usedFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (firstFlagIdx >= 0)
        {
            for (var i = firstFlagIdx; i < tokens.Length; i++)
            {
                if (tokens[i].StartsWith('-'))
                    usedFlags.Add(tokens[i]);
            }
        }

        // Determine the partial flag the user is currently typing, if any
        var flagPrefix = "";
        string acceptPrefix;

        if (!endsWithSpace && tokens.Length > 0 && tokens[^1].StartsWith('-'))
        {
            flagPrefix = tokens[^1];
            usedFlags.Remove(flagPrefix); // don't exclude the flag currently being typed
            acceptPrefix = buffer[..^flagPrefix.Length]; // strip the partial flag
        }
        else if (endsWithSpace)
        {
            acceptPrefix = buffer; // space already there; append directly
        }
        else
        {
            // Last token is a flag value — add a space so the next flag is cleanly appended
            acceptPrefix = buffer.TrimEnd() + " ";
        }

        return allFlags
            .Where(f => !usedFlags.Contains(f.Flag))
            .Where(f => flagPrefix.Length == 0 || f.Flag.StartsWith(flagPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(f => new CompletionItem(f.Flag, f.Description, acceptPrefix + f.Flag + " "))
            .ToList();
    }

    /// <summary>
    /// Searches for commands matching a prefix (with or without leading /).
    /// </summary>
    public IReadOnlyList<SlashCommandDescriptor> Search(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return AllCommands.AsReadOnly();

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

    private static CompletionItem ToCommandItem(SlashCommandDescriptor cmd) =>
        new(cmd.Command, cmd.Description, cmd.Command + " ");
}
