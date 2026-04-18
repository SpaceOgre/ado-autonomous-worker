using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ado.AutonomousWorker.Tests;

public sealed class ProcessCommandRunnerTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "ado-worker-tests",
        Guid.NewGuid().ToString("N")
    );

    public ProcessCommandRunnerTests()
    {
        Directory.CreateDirectory(_rootPath);
    }

    [Test]
    public async Task RunAsync_ThrowsTimeoutExceptionWhenProcessExceedsConfiguredTimeout()
    {
        var runner = new ProcessCommandRunner(NullLogger<ProcessCommandRunner>.Instance);
        var artifactPath = Path.Combine(_rootPath, "timeout.log");

        var exception = await Assert
            .That(async () =>
            {
                await runner.RunAsync(
                    "pwsh",
                    ["-NoLogo", "-NoProfile", "-Command", "Start-Sleep -Seconds 2"],
                    _rootPath,
                    artifactPath,
                    environmentVariables: null,
                    activityName: "Timeout test",
                    outputCallback: null,
                    cancellationToken: CancellationToken.None,
                    timeoutSeconds: 1
                );
            })
            .Throws<TimeoutException>();

        await Assert.That(exception!.Message).Contains("configured timeout of 1 seconds");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
