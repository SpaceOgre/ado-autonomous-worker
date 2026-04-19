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
    private IReadOnlyList<CompletionItem> _completions = [];
    private int _selectedIndex = -1;
    private int _previousSelectedIndex = -1;
    private bool _completionsVisible;
    private bool _selectionChangedOnly;
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
            return KeyAction.Submit; // run the accepted command immediately
        }

        return KeyAction.Submit;
    }

    private string SubmitLine()
    {
        ClearCompletionArea();
        RenderPromptLine(); // ensure the accepted command is visible before execution
        Console.WriteLine();
        var result = _buffer.ToString();
        if (!string.IsNullOrWhiteSpace(result))
            AddToHistory(result);
        return result;
    }

    private void HandleTab(bool reverse)
    {
        if (_completionsVisible && _completions.Count > 0)
        {
            if (_selectedIndex < 0)
                _selectedIndex = 0;
            var accepted = _completions[_selectedIndex];
            AcceptCompletion();
            // Tab opens the next completion level (flags/subcommands); Enter does not.
            if (!accepted.Label.StartsWith('-'))
                RefreshCompletions();
            return;
        }

        // Popup not visible: accept ghost text if present, then open completions.
        // This lets Tab work naturally after history navigation.
        if (_ghostText.Length > 0)
        {
            _buffer.Append(_ghostText);
            _cursor = _buffer.Length;
            _ghostText = string.Empty;
        }

        RefreshCompletions();
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

        _previousSelectedIndex = _selectedIndex;
        _selectedIndex += direction;
        if (_selectedIndex < 0)
            _selectedIndex = _completions.Count - 1;
        else if (_selectedIndex >= _completions.Count)
            _selectedIndex = 0;
        _selectionChangedOnly = true;
    }

    private void AcceptCompletion()
    {
        if (_selectedIndex >= 0 && _selectedIndex < _completions.Count)
            ApplyCompletion(_completions[_selectedIndex]);
        // ApplyCompletion owns visibility state; nothing else to do here
    }

    private void ApplyCompletion(CompletionItem item)
    {
        _buffer.Clear();
        _buffer.Append(item.AcceptText);
        _cursor = _buffer.Length;
        _completionsVisible = false;
        _selectedIndex = -1;
        _completions = [];
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
        var buffer = _buffer.ToString();
        _completions = _registry.GetCompletions(buffer);
        _selectionChangedOnly = false; // list changed; need full redraw

        if (buffer.Length == 0 || _completions.Count == 0)
        {
            _completionsVisible = false;
            _selectedIndex = -1;
        }
        else
        {
            _selectedIndex = 0;
            _completionsVisible = true;
        }
    }

    private void UpdateGhostText()
    {
        _ghostText = string.Empty;
        if (_cursor != _buffer.Length || _buffer.Length == 0 || _completionsVisible)
            return;

        var text = _buffer.ToString();
        var completions = _registry.GetCompletions(text);
        if (completions.Count > 0)
        {
            var best = completions[0];
            var acceptText = best.AcceptText.TrimEnd();
            if (acceptText.Length > text.Length && acceptText.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                _ghostText = acceptText[text.Length..];
        }
    }

    private void ResetState()
    {
        _buffer.Clear();
        _cursor = 0;
        _historyIndex = _history.Count;
        _completions = [];
        _selectedIndex = -1;
        _previousSelectedIndex = -1;
        _completionsVisible = false;
        _selectionChangedOnly = false;
        _completionLinesDrawn = 0;
        _ghostText = string.Empty;
    }

    // ─── Rendering ───────────────────────────────────────────────────

    private void RenderAll()
    {
        Console.Write("\x1b[?25l"); // hide cursor while rendering
        try
        {
            if (_selectionChangedOnly && _completionsVisible)
            {
                _selectionChangedOnly = false;
                RenderSelectionUpdate();
                var col = PromptDisplayWidth + _cursor;
                Console.Write($"\r\x1b[{col + 1}G");
                return;
            }

            _selectionChangedOnly = false;
            ClearCompletionArea();
            RenderPromptLine();

            if (_completionsVisible && _completions.Count > 0)
                RenderCompletionPopup();

            PositionCursor();
        }
        finally
        {
            Console.Write("\x1b[?25h"); // restore cursor
        }
    }

    // Targeted update: redraws only the two rows whose selection state changed.
    // Cursor must be at the prompt line on entry and will be there on return.
    // Item row i sits (i + 2) lines below the prompt: +1 for the separator newline, +1 for the top border.
    private void RenderSelectionUpdate()
    {
        var items =
            _completions.Count <= MaxCompletionItems ? _completions : _completions.Take(MaxCompletionItems).ToList();
        if (items.Count == 0)
            return;

        var maxLabelLen = items.Max(c => c.Label.Length);
        var maxDescLen = items.Max(c => c.Description.Length);
        const int indicatorPrefixWidth = 3;
        const int columnSpacingWidth = 2;
        var innerWidth = indicatorPrefixWidth + maxLabelLen + columnSpacingWidth + maxDescLen;

        foreach (var idx in new[] { _previousSelectedIndex, _selectedIndex })
        {
            if (idx < 0 || idx >= items.Count)
                continue;

            var item = items[idx];
            var lineOffset = idx + 2;
            var isSelected = idx == _selectedIndex;
            var indicator = isSelected ? "\x1b[38;5;39m▸\x1b[0m" : " ";
            var labelPadded = item.Label.PadRight(maxLabelLen);
            var desc = item.Description;

            Console.Write($"\x1b[{lineOffset}B\r"); // move down to item row

            if (isSelected)
                Console.Write(
                    $"  \x1b[38;5;240m│\x1b[0m {indicator} \x1b[38;5;39;1m{labelPadded}\x1b[0m  \x1b[38;5;248m{desc}\x1b[0m"
                );
            else
                Console.Write(
                    $"  \x1b[38;5;240m│\x1b[0m {indicator} \x1b[38;5;252m{labelPadded}\x1b[0m  \x1b[38;5;243m{desc}\x1b[0m"
                );

            var contentWidth = indicatorPrefixWidth + labelPadded.Length + columnSpacingWidth + desc.Length;
            var rightPad = innerWidth - contentWidth;
            if (rightPad > 0)
                Console.Write(new string(' ', rightPad));
            Console.Write("\x1b[38;5;240m│\x1b[0m");

            Console.Write($"\x1b[{lineOffset}A"); // move back up to prompt line
        }
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
        var maxLabelLen = items.Max(c => c.Label.Length);
        var maxDescLen = items.Max(c => c.Description.Length);
        const int indicatorPrefixWidth = 3; // " {indicator} "
        const int columnSpacingWidth = 2; // spacing between label and description
        var innerWidth = indicatorPrefixWidth + maxLabelLen + columnSpacingWidth + maxDescLen;

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
            var labelPadded = item.Label.PadRight(maxLabelLen);
            var desc = item.Description;

            if (isSelected)
            {
                Console.Write(
                    $"  \x1b[38;5;240m│\x1b[0m {indicator} \x1b[38;5;39;1m{labelPadded}\x1b[0m  \x1b[38;5;248m{desc}\x1b[0m"
                );
            }
            else
            {
                Console.Write(
                    $"  \x1b[38;5;240m│\x1b[0m {indicator} \x1b[38;5;252m{labelPadded}\x1b[0m  \x1b[38;5;243m{desc}\x1b[0m"
                );
            }

            // Right border padding
            var contentWidth = indicatorPrefixWidth + labelPadded.Length + columnSpacingWidth + desc.Length;
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

        // Popup is below the prompt line. Move down to its bottom, then erase upward.
        Console.Write($"\x1b[{_completionLinesDrawn}B");
        for (var i = 0; i < _completionLinesDrawn; i++)
        {
            Console.Write("\r\x1b[K");
            Console.Write("\x1b[A");
        }
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
