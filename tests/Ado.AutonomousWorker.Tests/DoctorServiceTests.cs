using System.Reflection;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Diagnostics;

namespace Ado.AutonomousWorker.Tests;

public sealed class DoctorServiceTests
{
    [Theory]
    [InlineData(AgentProvider.Claude, "claude")]
    [InlineData(AgentProvider.Codex, "codex")]
    public void CheckAgentCommand_UsesProviderDefaultWhenCommandPathIsEmpty(AgentProvider provider, string expectedCommandName)
    {
        var method = typeof(DoctorService).GetMethod("CheckAgentCommand", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (DoctorCheckResult?)method!.Invoke(
            null,
            ["Agent", new AgentRoleConfig { Provider = provider, CommandPath = string.Empty }]);

        Assert.NotNull(result);
        Assert.NotEqual("No command path configured", result!.Detail);
        Assert.StartsWith(expectedCommandName, Path.GetFileNameWithoutExtension(result.Detail), StringComparison.OrdinalIgnoreCase);
    }
}
