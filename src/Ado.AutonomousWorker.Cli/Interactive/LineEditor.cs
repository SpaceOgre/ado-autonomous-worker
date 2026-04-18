using System.Text;

namespace Ado.AutonomousWorker.Cli.Interactive;

/// <summary>
/// Rich line editor with tab completion, ghost text suggestions,
/// command history, and keyboard shortcuts. Designed for interactive REPL use.
/// </summary>
public sealed class LineEditor
{
    private const string PromptPrefix = "\x1b[38;5;39m❯\x1b[0m "; // deepskyblue1
    private const int PromptDisplayWidth = 2; // "❯ " visible width
    private const int MaxCompletionItems = 8;

    private readonly SlashCommandRegistry _registry;
    private readonly List<string> _history = [];
    private int _historyIndex;

    // Buffer state
    private readonly StringBuilder _buffer = new();
    private int _cursor;

    // Completion state
    private IReadOnlyList<SlashCommandDescriptor> _completions = [];
    private int _selectedIndex = -1;
    private bool _completionsVisible;
    private int _completionLinesDrawn;

    // Ghost text
    private string _ghostText = string.Empty;

    public LineEditor(SlashCommandRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Reads a line of input with interactive completion support.
    /// Returns null if cancelled (Ctrl+C), the input string on Enter.
    /// </summary>
    public string? ReadLine(CancellationToken ct)
    {
        ResetState();
        RenderAll();

        while (!ct.IsCancellationRequested)
        {
            // Wait for key availability, checking cancellation periodically.
            while (!Console.KeyAvailable)
            {
                if (ct.IsCancellationRequested)
                    return null;
                Thread.Sleep(50);
            }

            var key = Console.ReadKey(intercept: true);

            if (key is { Key: ConsoleKey.C, Modifiers: ConsoleModifiers.Control })
            {
                ClearCompletionArea();
                Console.WriteLine();
                return null;
            }

            var handled = HandleKey(key);
            if (handled == KeyAction.Submit)
            {
                var result = SubmitLine();
                return result;
            }

            if (handled == KeyAction.ClearScreen)
            {
                ClearCompletionArea();
                Console.Clear();
            }

            UpdateGhostText();
            RenderAll();
        }

        return null;
    }

    /// <summary>Adds a command to the history.</summary>
    public void AddToHistory(string line)
    {
        if (
            !string.IsNullOrWhiteSpace(line)
            && (_history.Count == 0 || !string.Equals(_history[^1], line, StringComparison.Ordinal))
        )
        {
            _history.Add(line);
        }
    }

    private enum KeyAction
    {
        Continue,
        Submit,
        ClearScreen,
    }

    private KeyAction HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                return HandleEnter();

            case ConsoleKey.Tab:
                HandleTab(reverse: (key.Modifiers & ConsoleModifiers.Shift) != 0);
                return KeyAction.Continue;

            case ConsoleKey.Escape:
                HandleEscape();
                return KeyAction.Continue;

            case ConsoleKey.Backspace:
                HandleBackspace();
                return KeyAction.Continue;

            case ConsoleKey.Delete:
                HandleDelete();
                return KeyAction.Continue;

            case ConsoleKey.LeftArrow:
                if (_cursor > 0)
                    _cursor--;
                return KeyAction.Continue;

            case ConsoleKey.RightArrow:
                return HandleRightArrow();

            case ConsoleKey.UpArrow:
                if (_completionsVisible)
                    MoveSelection(-1);
                else
                    NavigateHistory(-1);
                return KeyAction.Continue;

            case ConsoleKey.DownArrow:
                if (_completionsVisible)
                    MoveSelection(1);
                else
                    NavigateHistory(1);
                return KeyAction.Continue;

            case ConsoleKey.Home:
                _cursor = 0;
                return KeyAction.Continue;

            case ConsoleKey.End:
                _cursor = _buffer.Length;
                return KeyAction.Continue;

            default:
                return HandleDefaultKey(key);
        }
    }

    private KeyAction HandleEnter()
    {
        if (_completionsVisible && _selectedIndex >= 0 && _selectedIndex < _completions.Count)
        {
            AcceptCompletion();
            return KeyAction.Continue;
        }

        return KeyAction.Submit;
    }

    private string SubmitLine()
    {
        ClearCompletionArea();
        Console.WriteLine();
        var result = _buffer.ToString();
        if (!string.IsNullOrWhiteSpace(result))
            AddToHistory(result);
        return result;
    }

    private void HandleTab(bool reverse)
    {
        if (!_completionsVisible)
        {
            // First tab press: update completions and show
            var prefix = _buffer.ToString();
            _completions = _registry.Search(prefix);
            if (_completions.Count == 1)
            {
                // Single match: auto-complete immediately
                ApplyCompletion(_completions[0]);
                _completionsVisible = false;
                return;
            }

            if (_completions.Count > 0)
            {
                _completionsVisible = true;
                _selectedIndex = 0;
            }

            return;
        }

        // Cycle through completions
        MoveSelection(reverse ? -1 : 1);
    }

    private void HandleEscape()
    {
        if (_completionsVisible)
        {
            _completionsVisible = false;
            _selectedIndex = -1;
            _completions = [];
        }
        else
        {
            _buffer.Clear();
            _cursor = 0;
        }
    }

    private void HandleBackspace()
    {
        if (_cursor <= 0)
            return;

        _buffer.Remove(_cursor - 1, 1);
        _cursor--;
        RefreshCompletions();
    }

    private void HandleDelete()
    {
        if (_cursor >= _buffer.Length)
            return;

        _buffer.Remove(_cursor, 1);
        RefreshCompletions();
    }

    private KeyAction HandleRightArrow()
    {
        if (_cursor < _buffer.Length)
        {
            _cursor++;
        }
        else if (_ghostText.Length > 0)
        {
            // Accept ghost text
            _buffer.Append(_ghostText);
            _cursor = _buffer.Length;
            _ghostText = string.Empty;
            RefreshCompletions();
        }

        return KeyAction.Continue;
    }

    private KeyAction HandleDefaultKey(ConsoleKeyInfo key)
    {
        if (key is { Key: ConsoleKey.L, Modifiers: ConsoleModifiers.Control })
            return KeyAction.ClearScreen;

        if (key is { Key: ConsoleKey.U, Modifiers: ConsoleModifiers.Control })
        {
            _buffer.Clear();
            _cursor = 0;
            RefreshCompletions();
            return KeyAction.Continue;
        }

        if (key is { Key: ConsoleKey.W, Modifiers: ConsoleModifiers.Control })
        {
            DeleteWordBack();
            RefreshCompletions();
            return KeyAction.Continue;
        }

        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
        {
            _buffer.Insert(_cursor, key.KeyChar);
            _cursor++;
            RefreshCompletions();
        }

        return KeyAction.Continue;
    }

    private void MoveSelection(int direction)
    {
        if (_completions.Count == 0)
            return;

        _selectedIndex += direction;
        if (_selectedIndex < 0)
            _selectedIndex = _completions.Count - 1;
        else if (_selectedIndex >= _completions.Count)
            _selectedIndex = 0;
    }

    private void AcceptCompletion()
    {
        if (_selectedIndex >= 0 && _selectedIndex < _completions.Count)
        {
            ApplyCompletion(_completions[_selectedIndex]);
        }

        _completionsVisible = false;
        _selectedIndex = -1;
    }

    private void ApplyCompletion(SlashCommandDescriptor cmd)
    {
        _buffer.Clear();
        _buffer.Append(cmd.Command);
        // Add trailing space so user can start typing args
        if (!cmd.Command.Contains(' ') || HasSubcommand(cmd.Command))
            _buffer.Append(' ');
        _cursor = _buffer.Length;
        _completionsVisible = false;
        _selectedIndex = -1;
        _completions = [];
    }

    private static bool HasSubcommand(string command)
    {
        // Commands like "/task run" already have a subcommand
        return command.TrimStart('/').Contains(' ');
    }

    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0)
            return;

        _historyIndex += direction;
        _historyIndex = Math.Clamp(_historyIndex, 0, _history.Count);

        _buffer.Clear();
        if (_historyIndex < _history.Count)
            _buffer.Append(_history[_historyIndex]);
        _cursor = _buffer.Length;
    }

    private void DeleteWordBack()
    {
        if (_cursor == 0)
            return;

        var end = _cursor;
        // Skip trailing spaces
        while (_cursor > 0 && _buffer[_cursor - 1] == ' ')
            _cursor--;
        // Delete word
        while (_cursor > 0 && _buffer[_cursor - 1] != ' ')
            _cursor--;

        _buffer.Remove(_cursor, end - _cursor);
    }

    private void RefreshCompletions()
    {
        if (!_completionsVisible)
            return;

        var prefix = _buffer.ToString();
        _completions = _registry.Search(prefix);
        if (_completions.Count == 0)
        {
            _completionsVisible = false;
            _selectedIndex = -1;
        }
        else
        {
            _selectedIndex = Math.Min(_selectedIndex, _completions.Count - 1);
        }
    }

    private void UpdateGhostText()
    {
        _ghostText = string.Empty;
        if (_cursor != _buffer.Length || _buffer.Length == 0 || _completionsVisible)
            return;

        var text = _buffer.ToString();
        var matches = _registry.Search(text);
        if (matches.Count > 0)
        {
            var best = matches[0];
            if (best.Command.Length > text.Length && best.Command.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                _ghostText = best.Command[text.Length..];
            }
        }
    }

    private void ResetState()
    {
        _buffer.Clear();
        _cursor = 0;
        _historyIndex = _history.Count;
        _completions = [];
        _selectedIndex = -1;
        _completionsVisible = false;
        _completionLinesDrawn = 0;
        _ghostText = string.Empty;
    }

    // ─── Rendering ───────────────────────────────────────────────────

    private void RenderAll()
    {
        ClearCompletionArea();
        RenderPromptLine();

        if (_completionsVisible && _completions.Count > 0)
            RenderCompletionPopup();

        PositionCursor();
    }

    private void RenderPromptLine()
    {
        // Move to beginning of current line and clear
        Console.Write("\r\x1b[K");

        // Write prompt
        Console.Write(PromptPrefix);

        // Write buffer text (before and after cursor in white)
        var text = _buffer.ToString();
        Console.Write(text);

        // Write ghost text in dim grey
        if (_ghostText.Length > 0)
        {
            Console.Write($"\x1b[90m{_ghostText}\x1b[0m");
        }
    }

    private void RenderCompletionPopup()
    {
        var items =
            _completions.Count <= MaxCompletionItems ? _completions : _completions.Take(MaxCompletionItems).ToList();

        // Calculate column widths
        var maxCmdLen = items.Max(c => c.Command.Length);
        var maxDescLen = items.Max(c => c.Description.Length);
        var innerWidth = maxCmdLen + 2 + maxDescLen + 2; // padding

        Console.WriteLine();

        // Top border
        var border = $"  \x1b[38;5;240m┌{new string('─', innerWidth)}┐\x1b[0m";
        Console.Write(border);
        _completionLinesDrawn = 1;

        foreach (var (item, index) in items.Select((item, index) => (item, index)))
        {
            Console.WriteLine();
            _completionLinesDrawn++;

            var isSelected = index == _selectedIndex;
            var indicator = isSelected ? "\x1b[38;5;39m▸\x1b[0m" : " ";
            var cmdPadded = item.Command.PadRight(maxCmdLen);
            var desc = item.Description;

            if (isSelected)
            {
                // Selected item: highlight command in deepskyblue1
                Console.Write(
                    $"  \x1b[38;5;240m│\x1b[0m {indicator} \x1b[38;5;39;1m{cmdPadded}\x1b[0m  \x1b[38;5;248m{desc}\x1b[0m"
                );
            }
            else
            {
                // Normal item: command in white, description in grey
                Console.Write(
                    $"  \x1b[38;5;240m│\x1b[0m {indicator} \x1b[38;5;252m{cmdPadded}\x1b[0m  \x1b[38;5;243m{desc}\x1b[0m"
                );
            }

            // Right border padding
            var contentWidth = 1 + 1 + cmdPadded.Length + 2 + desc.Length;
            var rightPad = innerWidth - contentWidth;
            if (rightPad > 0)
                Console.Write(new string(' ', rightPad));
            Console.Write("\x1b[38;5;240m│\x1b[0m");
        }

        if (_completions.Count > MaxCompletionItems)
        {
            Console.WriteLine();
            _completionLinesDrawn++;
            var moreText = $"  +{_completions.Count - MaxCompletionItems} more...";
            Console.Write(
                $"  \x1b[38;5;240m│\x1b[0m \x1b[38;5;243m{moreText.PadRight(innerWidth - 1)}\x1b[0m\x1b[38;5;240m│\x1b[0m"
            );
        }

        // Bottom border
        Console.WriteLine();
        _completionLinesDrawn++;
        Console.Write($"  \x1b[38;5;240m└{new string('─', innerWidth)}┘\x1b[0m");
    }

    private void ClearCompletionArea()
    {
        if (_completionLinesDrawn <= 0)
            return;

        // Move up and clear each line of the completion popup
        for (var i = 0; i < _completionLinesDrawn; i++)
        {
            Console.Write("\x1b[K"); // Clear current line
            if (i < _completionLinesDrawn - 1)
                Console.Write("\x1b[A"); // Move up
        }

        // Move back up to the prompt line
        Console.Write("\r");
        _completionLinesDrawn = 0;
    }

    private void PositionCursor()
    {
        if (_completionsVisible && _completionLinesDrawn > 0)
        {
            // Move cursor back up to the prompt line
            Console.Write($"\x1b[{_completionLinesDrawn}A");
        }

        // Position cursor at the correct column
        var col = PromptDisplayWidth + _cursor;
        Console.Write($"\r\x1b[{col + 1}G"); // 1-based column
    }
}
