using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ado.AutonomousWorker.Cli;

internal sealed class WakeLock : IDisposable
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    public WakeLock()
    {
        if (OperatingSystem.IsWindows())
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            SetThreadExecutionState(ES_CONTINUOUS);
    }
}
