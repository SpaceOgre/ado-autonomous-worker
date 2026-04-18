using System.Diagnostics;
using System.Text;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Microsoft.Extensions.Logging;

namespace Ado.AutonomousWorker.Infrastructure.Execution;

public sealed class ProcessCommandRunner(ILogger<ProcessCommandRunner> logger)
{
    public async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string artifactPath,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        string? activityName,
        Func<ProcessOutputLine, CancellationToken, Task>? outputCallback,
        CancellationToken cancellationToken,
        int? timeoutSeconds = null
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        var resolvedFileName = CommandPathResolver.ResolveOrOriginal(fileName);
        var (startFileName, startArguments) = PrepareProcessStart(resolvedFileName, arguments);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = startFileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (startArguments.RawArguments is not null)
        {
            // For .CMD/.BAT files the whole invocation is pre-built as a single raw
            // command-line string and must be passed via Arguments (not ArgumentList).
            // Using ArgumentList would cause .NET to re-escape the string with \"...\"
            // which cmd.exe does not recognise — it uses "" for embedded quotes — and
            // that re-escaping splits quoted tokens into separate words at spaces.
            process.StartInfo.Arguments = startArguments.RawArguments;
        }
        else
        {
            foreach (var argument in startArguments.List!)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
        }

        if (environmentVariables is not null)
        {
            foreach (var pair in environmentVariables)
            {
                if (pair.Value is null)
                {
                    continue;
                }

                process.StartInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var timeoutCts = timeoutSeconds.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        if (timeoutSeconds.HasValue && timeoutCts is not null)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds.Value));
        }

        var effectiveCancellationToken = timeoutCts?.Token ?? cancellationToken;
        var stopwatch = Stopwatch.StartNew();
        if (!string.IsNullOrWhiteSpace(activityName))
        {
            logger.LogInformation("{Activity} started.", activityName);
        }

        process.Start();
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var stdoutTask = PumpOutputAsync(
            process.StandardOutput,
            AgentOutputStream.Stdout,
            stdoutBuilder,
            outputCallback,
            effectiveCancellationToken
        );
        var stderrTask = PumpOutputAsync(
            process.StandardError,
            AgentOutputStream.Stderr,
            stderrBuilder,
            outputCallback,
            effectiveCancellationToken
        );
        var waitForExitTask = process.WaitForExitAsync(effectiveCancellationToken);
        var heartbeatInterval = TimeSpan.FromSeconds(15);

        while (!waitForExitTask.IsCompleted)
        {
            await Task.WhenAny(waitForExitTask, Task.Delay(heartbeatInterval, effectiveCancellationToken));
            if (!waitForExitTask.IsCompleted && !string.IsNullOrWhiteSpace(activityName))
            {
                logger.LogInformation(
                    "{Activity} still running after {ElapsedSeconds}s.",
                    activityName,
                    (int)stopwatch.Elapsed.TotalSeconds
                );
            }
        }

        try
        {
            await waitForExitTask;
        }
        catch (OperationCanceledException) when (IsTimeoutCancellation(timeoutCts, cancellationToken))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception killException)
            {
                logger.LogWarning(
                    killException,
                    "Failed to terminate timed out process for {Activity}.",
                    activityName ?? fileName
                );
            }

            throw new TimeoutException(
                $"'{activityName ?? fileName}' exceeded the configured timeout of {timeoutSeconds} seconds."
            );
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        var transcript = $$"""
            Command: {{resolvedFileName}} {{string.Join(" ", arguments)}}
            ExitCode: {{process.ExitCode}}

            STDOUT
            {{stdout}}

            STDERR
            {{stderr}}
            """;

        await File.WriteAllTextAsync(artifactPath, transcript, cancellationToken);
        if (!string.IsNullOrWhiteSpace(activityName))
        {
            logger.LogInformation(
                "{Activity} finished with exit code {ExitCode} after {ElapsedSeconds}s.",
                activityName,
                process.ExitCode,
                (int)stopwatch.Elapsed.TotalSeconds
            );
        }

        return new CommandResult($"{resolvedFileName} {string.Join(" ", arguments)}", process.ExitCode, stdout, stderr);
    }

    public Task<CommandResult> RunPowerShellAsync(
        string script,
        string workingDirectory,
        string artifactPath,
        CancellationToken cancellationToken
    )
    {
        return RunAsync(
            "pwsh",
            ["-NoLogo", "-NoProfile", "-Command", script],
            workingDirectory,
            artifactPath,
            environmentVariables: null,
            activityName: $"PowerShell: {script}",
            outputCallback: null,
            cancellationToken
        );
    }

    private static bool IsTimeoutCancellation(
        CancellationTokenSource? timeoutCts,
        CancellationToken outerCancellationToken
    )
    {
        return timeoutCts is not null
            && timeoutCts.IsCancellationRequested
            && !outerCancellationToken.IsCancellationRequested;
    }

    private static (string FileName, ProcessArguments Arguments) PrepareProcessStart(
        string resolvedFileName,
        IReadOnlyList<string> arguments
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            return (resolvedFileName, new ProcessArguments(arguments));
        }

        var extension = Path.GetExtension(resolvedFileName);
        if (
            !string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
        )
        {
            return (resolvedFileName, new ProcessArguments(arguments));
        }

        var shellCommand = string.Join(
            " ",
            new[] { QuoteForCmd(resolvedFileName) }.Concat(arguments.Select(QuoteForCmd))
        );

        // Wrap the shellCommand in outer quotes so that cmd.exe /s /c strips exactly
        // the outermost pair and leaves the inner quoted tokens (e.g. the prompt argument)
        // intact.  Passing shellCommand as a separate ArgumentList entry would cause .NET
        // to re-escape every " as \" which cmd.exe does not handle.
        var rawArguments = $"/d /s /c \"{shellCommand}\"";
        return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", new ProcessArguments(rawArguments));
    }

    private readonly record struct ProcessArguments(IReadOnlyList<string>? List, string? RawArguments)
    {
        public ProcessArguments(IReadOnlyList<string> list)
            : this(list, null) { }

        public ProcessArguments(string raw)
            : this(null, raw) { }
    }

    private static string QuoteForCmd(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var escaped = value.Replace("\"", "\"\"");
        return value.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')')
            ? $"\"{escaped}\""
            : escaped;
    }

    private static async Task PumpOutputAsync(
        StreamReader reader,
        AgentOutputStream stream,
        StringBuilder builder,
        Func<ProcessOutputLine, CancellationToken, Task>? outputCallback,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            builder.AppendLine(line);
            if (outputCallback is not null)
            {
                await outputCallback(new ProcessOutputLine(stream, line), cancellationToken);
            }
        }
    }
}
