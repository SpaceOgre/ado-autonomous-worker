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
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
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

        var stopwatch = Stopwatch.StartNew();
        if (!string.IsNullOrWhiteSpace(activityName))
        {
            logger.LogInformation("{Activity} started.", activityName);
        }

        process.Start();
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var stdoutTask = PumpOutputAsync(process.StandardOutput, AgentOutputStream.Stdout, stdoutBuilder, outputCallback, cancellationToken);
        var stderrTask = PumpOutputAsync(process.StandardError, AgentOutputStream.Stderr, stderrBuilder, outputCallback, cancellationToken);
        var waitForExitTask = process.WaitForExitAsync(cancellationToken);
        var heartbeatInterval = TimeSpan.FromSeconds(15);

        while (!waitForExitTask.IsCompleted)
        {
            await Task.WhenAny(waitForExitTask, Task.Delay(heartbeatInterval, cancellationToken));
            if (!waitForExitTask.IsCompleted && !string.IsNullOrWhiteSpace(activityName))
            {
                logger.LogInformation("{Activity} still running after {ElapsedSeconds}s.", activityName, (int)stopwatch.Elapsed.TotalSeconds);
            }
        }

        await waitForExitTask;
        await Task.WhenAll(stdoutTask, stderrTask);
        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        var transcript = $$"""
        Command: {{fileName}} {{string.Join(" ", arguments)}}
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
                (int)stopwatch.Elapsed.TotalSeconds);
        }

        return new CommandResult($"{fileName} {string.Join(" ", arguments)}", process.ExitCode, stdout, stderr);
    }

    public Task<CommandResult> RunPowerShellAsync(
        string script,
        string workingDirectory,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        return RunAsync(
            "pwsh",
            ["-NoLogo", "-NoProfile", "-Command", script],
            workingDirectory,
            artifactPath,
            environmentVariables: null,
            activityName: $"PowerShell: {script}",
            outputCallback: null,
            cancellationToken);
    }

    private static async Task PumpOutputAsync(
        StreamReader reader,
        AgentOutputStream stream,
        StringBuilder builder,
        Func<ProcessOutputLine, CancellationToken, Task>? outputCallback,
        CancellationToken cancellationToken)
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
