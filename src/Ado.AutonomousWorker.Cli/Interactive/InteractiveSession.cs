using Ado.AutonomousWorker.Cli.Presentation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Interactive;

/// <summary>
/// Main REPL session for interactive CLI mode.
/// Displays a welcome screen, processes slash commands, and delegates
/// non-built-in commands to the Spectre.Console CommandApp.
/// </summary>
public sealed class InteractiveSession(IServiceProvider services)
{
    private readonly SlashCommandRegistry _registry = new();
    private bool _exitRequested;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var app = CreateCommandApp();
        var editor = new LineEditor(_registry);

        RenderWelcome();

        while (!ct.IsCancellationRequested && !_exitRequested)
        {
            string? input;
            try
            {
                input = editor.ReadLine(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (input is null)
            {
                // Ctrl+C pressed
                RenderGoodbye();
                return 0;
            }

            var trimmed = input.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // Handle built-in commands
            if (HandleBuiltin(trimmed))
                continue;

            // Delegate to CommandApp
            if (trimmed.StartsWith('/'))
            {
                var args = ParseSlashCommand(trimmed);
                try
                {
                    await app.RunAsync(args);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                }
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Unknown input.[/] Commands start with [deepskyblue1]/[/]. Type [deepskyblue1]/help[/] for available commands."
                );
            }

            AnsiConsole.WriteLine();
        }

        RenderGoodbye();
        return 0;
    }

    private CommandApp CreateCommandApp()
    {
        var app = new CommandApp(new TypeRegistrar(services));
        app.Configure(CliAppConfiguration.Configure);
        return app;
    }

    private bool HandleBuiltin(string input)
    {
        var normalized = input.TrimStart('/').Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "help":
            case "help -a":
            case "help --all":
                RenderHelp();
                return true;

            case "clear":
                Console.Clear();
                return true;

            case "exit":
            case "quit":
            case "q":
                _exitRequested = true;
                return true;

            default:
                return false;
        }
    }

    private static string[] ParseSlashCommand(string input)
    {
        // Remove leading / and tokenize with shell-style quote handling
        var command = input.TrimStart('/');
        return TokenizeCommand(command);
    }

    private static string[] TokenizeCommand(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inSingleQuotes = false;
        var inDoubleQuotes = false;
        var escaping = false;

        foreach (var ch in command)
        {
            if (escaping)
            {
                current.Append(ch);
                escaping = false;
                continue;
            }

            if (ch == '\\')
            {
                escaping = true;
                continue;
            }

            if (ch == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }

            if (ch == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inSingleQuotes && !inDoubleQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (escaping)
        {
            current.Append('\\');
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens.ToArray();
    }

    // ─── Rendering ───────────────────────────────────────────────────

    private void RenderWelcome()
    {
        var version = typeof(InteractiveSession).Assembly.GetName().Version;
        var versionText = version is not null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "dev";

        AnsiConsole.WriteLine();

        var content = new Rows(
            new Markup($"[bold deepskyblue1]ado-worker[/] [grey]{Markup.Escape(versionText)}[/]"),
            new Markup("[grey]Autonomous Azure DevOps backlog worker[/]"),
            Text.Empty,
            new Markup(
                "[grey]Type[/] [deepskyblue1]/help[/] [grey]for commands[/]  [grey dim]·[/]  [grey]Tab for completion[/]  [grey dim]·[/]  [deepskyblue1]/exit[/] [grey]to quit[/]"
            )
        );

        var panel = new Panel(content)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.DeepSkyBlue1),
            Padding = new Padding(2, 0, 2, 0),
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private void RenderHelp()
    {
        AnsiConsole.WriteLine();

        // Commands by category
        var table = new Table { Border = TableBorder.None };
        table.AddColumn(new TableColumn("Command").PadRight(4));
        table.AddColumn(new TableColumn("Description"));
        table.HideHeaders();

        foreach (var category in _registry.GetCategories())
        {
            table.AddRow($"[bold deepskyblue1]{Markup.Escape(category)}[/]", string.Empty);

            foreach (var cmd in _registry.GetByCategory(category))
            {
                var style = cmd.IsBuiltin ? "mediumpurple2" : "white";
                table.AddRow(
                    $"  [{style}]{Markup.Escape(cmd.Command)}[/]",
                    $"[grey]{Markup.Escape(cmd.Description)}[/]"
                );
            }

            table.AddEmptyRow();
        }

        AnsiConsole.Write(table);

        // Keyboard shortcuts
        AnsiConsole.Write(CliStyle.SectionRule("Keyboard Shortcuts", "deepskyblue1"));
        AnsiConsole.WriteLine();

        var shortcuts = new Table { Border = TableBorder.None };
        shortcuts.AddColumn(new TableColumn("Key").PadRight(4));
        shortcuts.AddColumn(new TableColumn("Action"));
        shortcuts.HideHeaders();

        shortcuts.AddRow("[deepskyblue1]Tab[/]", "[grey]Complete command / cycle suggestions[/]");
        shortcuts.AddRow("[deepskyblue1]Shift+Tab[/]", "[grey]Cycle suggestions in reverse[/]");
        shortcuts.AddRow("[deepskyblue1]↑ / ↓[/]", "[grey]Navigate history or completion list[/]");
        shortcuts.AddRow("[deepskyblue1]→[/]", "[grey]Accept ghost text suggestion[/]");
        shortcuts.AddRow("[deepskyblue1]Esc[/]", "[grey]Dismiss completions / clear input[/]");
        shortcuts.AddRow("[deepskyblue1]Ctrl+L[/]", "[grey]Clear the screen[/]");
        shortcuts.AddRow("[deepskyblue1]Ctrl+U[/]", "[grey]Clear the input line[/]");
        shortcuts.AddRow("[deepskyblue1]Ctrl+W[/]", "[grey]Delete word backward[/]");
        shortcuts.AddRow("[deepskyblue1]Ctrl+C[/]", "[grey]Exit interactive mode[/]");

        AnsiConsole.Write(shortcuts);
        AnsiConsole.WriteLine();
    }

    private static void RenderGoodbye()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Markup("[grey]Goodbye![/]"));
        AnsiConsole.WriteLine();
    }
}
