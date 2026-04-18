using Spectre.Console;

namespace Ado.AutonomousWorker.Cli.Presentation;

public sealed class TerminalTitleService
{
    private readonly object _lock = new();
    private string? _lastTitle;

    public IDisposable BeginScope()
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return NoopDisposable.Instance;
        }

        return new Scope(this, TryGetCurrentTitle());
    }

    public void UpdateTitle(string title)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return;
        }

        lock (_lock)
        {
            if (string.Equals(_lastTitle, title, StringComparison.Ordinal))
            {
                return;
            }

            TrySetTitle(title);
            _lastTitle = title;
        }
    }

    private void RestoreTitle(string? title)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        lock (_lock)
        {
            TrySetTitle(title);
            _lastTitle = title;
        }
    }

    private static string? TryGetCurrentTitle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return Console.Title;
        }
        catch
        {
            return null;
        }
    }

    private static void TrySetTitle(string title)
    {
        var sanitizedTitle = title.ReplaceLineEndings(" ").Trim();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                Console.Title = sanitizedTitle;
            }
            catch
            {
                // Ignore terminals that do not support direct title updates.
            }
        }

        if (!AnsiConsole.Profile.Capabilities.Ansi)
        {
            return;
        }

        try
        {
            Console.Write($"\u001b]0;{sanitizedTitle}\u0007");
        }
        catch
        {
            // Ignore terminals that reject ANSI title sequences.
        }
    }

    private sealed class Scope(TerminalTitleService owner, string? originalTitle) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            owner.RestoreTitle(originalTitle);
            _disposed = true;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose() { }
    }
}
