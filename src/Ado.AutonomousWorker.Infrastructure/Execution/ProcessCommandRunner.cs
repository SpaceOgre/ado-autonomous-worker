using System.Diagnostics;
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
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
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
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

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
            cancellationToken);
    }
}
