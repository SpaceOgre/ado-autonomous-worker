using Ado.AutonomousWorker.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace Ado.AutonomousWorker.Infrastructure.Logging;

public static class WorkerLoggingSetup
{
    /// <summary>
    /// Creates the configured Serilog logger (console + rolling file).
    /// The caller is responsible for registering it with the host.
    /// </summary>
    public static ILogger CreateLogger(
        IConfiguration configuration,
        string? storageBaseOverride = null,
        string logFilePrefix = "worker"
    )
    {
        var earlyStorageOptions = new StorageOptions();
        configuration.GetSection("Storage").Bind(earlyStorageOptions);
        earlyStorageOptions.NormalizePaths(storageBaseOverride);
        Directory.CreateDirectory(earlyStorageOptions.LogRoot);

        var logFilePath = Path.Combine(earlyStorageOptions.LogRoot, $"{logFilePrefix}-.log");

        var configuredLevel = configuration["Serilog:ConsoleLevel"];
        var consoleLevel = Enum.TryParse<LogEventLevel>(configuredLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogEventLevel.Warning;

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                restrictedToMinimumLevel: consoleLevel,
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
            )
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger();
    }
}
