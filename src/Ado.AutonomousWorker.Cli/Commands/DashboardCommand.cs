using System.Diagnostics;
using System.Net.Sockets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class DashboardCommand : AsyncCommand<DashboardCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--port <PORT>")]
        public int Port { get; init; } = 5173;

        [CommandOption("--no-open")]
        public bool NoOpen { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var port = settings.Port;
        var url = $"http://localhost:{port}";

        AnsiConsole.MarkupLine($"[bold]ADO Autonomous Worker Dashboard[/]");
        AnsiConsole.MarkupLine($"Starting dashboard at [link={url}]{url}[/]");
        AnsiConsole.MarkupLine("[dim]Press Ctrl+C to stop.[/]");
        AnsiConsole.WriteLine();

        var (fileName, arguments) = ResolveDashboardProcess();

        if (fileName is null)
        {
            AnsiConsole.MarkupLine("[red]Could not locate the dashboard. Build the Ado.AutonomousWorker.Dashboard project first.[/]");
            return 1;
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.StartInfo.Environment["ASPNETCORE_URLS"] = url;
        process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";

        // Forward any AdoWorker_ config environment variables so the dashboard
        // picks up the same settings as the CLI.
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (key is not null && key.StartsWith("AdoWorker_", StringComparison.Ordinal))
            {
                process.StartInfo.Environment[key] = entry.Value?.ToString();
            }
        }

        process.Start();

        if (!settings.NoOpen)
        {
            await WaitForPortAsync(port, cancellationToken);
            OpenBrowser(url);
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: could not stop dashboard process: {Markup.Escape(ex.Message)}[/]");
            }
        }

        return process.HasExited ? process.ExitCode : 0;
    }

    private static (string? FileName, IReadOnlyList<string> Arguments) ResolveDashboardProcess()
    {
        // 1. Look for a published dashboard DLL next to this executable (installed tool scenario).
        var cliDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
        if (!string.IsNullOrEmpty(cliDir))
        {
            var dllPath = Path.Combine(cliDir, "Ado.AutonomousWorker.Dashboard.dll");
            if (File.Exists(dllPath))
            {
                return ("dotnet", [dllPath]);
            }
        }

        // 2. Walk up from the current directory looking for the Dashboard project file (dev scenario).
        var current = Directory.GetCurrentDirectory();
        for (var i = 0; i < 6; i++)
        {
            if (current is null) break;
            var projectPath = Path.Combine(current, "src", "Ado.AutonomousWorker.Dashboard", "Ado.AutonomousWorker.Dashboard.csproj");
            if (File.Exists(projectPath))
            {
                return ("dotnet", ["run", "--project", projectPath, "--no-launch-profile"]);
            }

            current = Path.GetDirectoryName(current);
        }

        return (null, []);
    }

    private static async Task WaitForPortAsync(int port, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port, cts.Token);
                return;
            }
            catch
            {
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
            }
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch
        {
            // Best-effort: if we can't open the browser, the user can navigate manually.
        }
    }
}
