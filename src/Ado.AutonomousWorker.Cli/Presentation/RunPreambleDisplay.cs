using Ado.AutonomousWorker.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Ado.AutonomousWorker.Cli.Presentation;

public sealed class RunPreambleDisplay
{
    private const string HeaderText = """
░░      ░░░       ░░░░      ░░░░░░░░░  ░░░░  ░░░      ░░░       ░░░  ░░░░  ░░        ░░       ░░
▒  ▒▒▒▒  ▒▒  ▒▒▒▒  ▒▒  ▒▒▒▒  ▒▒▒▒▒▒▒▒  ▒  ▒  ▒▒  ▒▒▒▒  ▒▒  ▒▒▒▒  ▒▒  ▒▒▒  ▒▒▒  ▒▒▒▒▒▒▒▒  ▒▒▒▒  ▒
▓  ▓▓▓▓  ▓▓  ▓▓▓▓  ▓▓  ▓▓▓▓  ▓▓▓▓▓▓▓▓        ▓▓  ▓▓▓▓  ▓▓       ▓▓▓     ▓▓▓▓▓      ▓▓▓▓       ▓▓
█        ██  ████  ██  ████  ████████   ██   ██  ████  ██  ███  ███  ███  ███  ████████  ███  ██
█  ████  ██       ████      █████████  ████  ███      ███  ████  ██  ████  ██        ██  ████  █
""";

    private static readonly string[] RainbowColors =
    [
        "red",
        "orange1",
        "yellow",
        "green",
        "deepskyblue1",
        "blue",
        "mediumpurple2",
        "hotpink",
    ];

    public void ShowSplash(string? status = null)
    {
        if (AnsiConsole.Profile.Capabilities.Interactive)
        {
            var rows = new List<IRenderable>
            {
                new Markup($"[deepskyblue1]{Markup.Escape(HeaderText)}[/]"),
                new Markup("[grey]Autonomous Azure DevOps backlog worker[/]"),
                new Markup(BuildAuthorMarkup()),
            };

            if (!string.IsNullOrWhiteSpace(status))
            {
                rows.Add(new Markup($"[yellow]{Markup.Escape(status)}[/]"));
            }

            AnsiConsole.Write(new Panel(new Rows(rows.ToArray()))
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.DeepSkyBlue1),
            });
            AnsiConsole.WriteLine();
            return;
        }

        AnsiConsole.MarkupLine("[deepskyblue1]ado-worker[/] Autonomous Azure DevOps backlog worker");
        AnsiConsole.MarkupLine(BuildAuthorMarkup());
        if (!string.IsNullOrWhiteSpace(status))
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(status)}[/]");
        }
    }

    public void ShowQueuePreview(IReadOnlyList<QueueItem> selectedQueue, int eligibleCount, int maxItemsPerRun, bool previewOnly)
    {
        var selectedText = selectedQueue.Count == 1 ? "item" : "items";
        var eligibleText = eligibleCount == 1 ? "item" : "items";
        var mode = previewOnly ? "Previewing" : "Processing";

        AnsiConsole.MarkupLine(
            $"[deepskyblue1]{mode}[/] {selectedQueue.Count} {selectedText} from {eligibleCount} eligible {eligibleText} ([grey]MaxItemsPerRun={maxItemsPerRun}[/]).");

        if (selectedQueue.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No eligible queue items found.[/]");
            return;
        }

        AnsiConsole.Write(QueueTableRenderer.CreateTable(selectedQueue, includeOrder: true));
        AnsiConsole.WriteLine();
    }

    private static string BuildAuthorMarkup()
    {
        if (!AnsiConsole.Profile.Capabilities.Ansi)
        {
            return "[grey]By SpaceOgre[/]";
        }

        var rainbowName = string.Concat(
            "SpaceOgre"
                .Select((character, index) => $"[{RainbowColors[index % RainbowColors.Length]}]{Markup.Escape(character.ToString())}[/]"));

        return $"[grey]By [/]{rainbowName}";
    }
}
