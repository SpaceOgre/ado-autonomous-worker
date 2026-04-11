using System.Diagnostics;
using Ado.AutonomousWorker.Core.Models;

namespace Ado.AutonomousWorker.Infrastructure.Execution;

public sealed class ProcessCommandRunner
{
    public async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string artifactPath,
        IReadOnlyDictionary<string, string?>? environmentVariables,
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

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
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
            cancellationToken);
    }
}
