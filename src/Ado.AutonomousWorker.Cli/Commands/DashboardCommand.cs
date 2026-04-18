using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class DashboardCommand : AsyncCommand<DashboardCommand.Settings>
{
    private const int DefaultPort = 5138;

    public sealed class Settings : CommandSettings
    {
        [Description("Port to start the dashboard on. Defaults to 5138.")]
        [CommandOption("--port <PORT>")]
        public int? Port { get; init; }

        [Description("Do not open the browser automatically after starting the dashboard.")]
        [CommandOption("--no-open")]
        public bool NoOpen { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var port = settings.Port ?? DefaultPort;
        var url = $"http://localhost:{port}";

        var (fileName, arguments) = ResolveDashboardProcess();

        AnsiConsole.MarkupLine($"Starting dashboard at [cornflowerblue]{url}[/]…");

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["ASPNETCORE_URLS"] = url;

        // Forward all AdoWorker_ env vars so the dashboard reads the same config as the CLI.
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key.ToString()!;
            if (key.StartsWith("AdoWorker_", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment[key] = entry.Value?.ToString() ?? string.Empty;
            }
        }

        using var process =
            Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start dashboard process.");

        if (!settings.NoOpen)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            OpenBrowser(url);
        }

        AnsiConsole.MarkupLine($"Dashboard running at [cornflowerblue]{url}[/]. Press [grey]Ctrl+C[/] to stop.");

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort
            }

            return 0;
        }
    }

    private static (string FileName, string[] Arguments) ResolveDashboardProcess()
    {
        var dashboardDll = Path.Combine(AppContext.BaseDirectory, "Ado.AutonomousWorker.Dashboard.dll");
        if (File.Exists(dashboardDll))
        {
            return ("dotnet", [dashboardDll]);
        }

        var projectFile = FindDashboardProject(AppContext.BaseDirectory);
        if (projectFile is not null)
        {
            return ("dotnet", ["run", "--project", projectFile, "--no-launch-profile"]);
        }

        // Fall back to the DLL path — the error from dotnet will explain what is missing.
        return ("dotnet", [dashboardDll]);
    }

    private static string? FindDashboardProject(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var projectPath = Path.Combine(
                dir.FullName,
                "src",
                "Ado.AutonomousWorker.Dashboard",
                "Ado.AutonomousWorker.Dashboard.csproj"
            );
            if (File.Exists(projectPath))
            {
                return projectPath;
            }

            dir = dir.Parent;
        }

        return null;
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
            // best-effort; ignore if browser launch fails
        }
    }
}
