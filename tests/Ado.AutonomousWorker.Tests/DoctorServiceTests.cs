using System.Reflection;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Diagnostics;

namespace Ado.AutonomousWorker.Tests;

public sealed class DoctorServiceTests
{
    [Test]
    [Arguments(AgentProvider.Claude, "claude")]
    [Arguments(AgentProvider.Codex, "codex")]
    public async Task CheckAgentCommand_UsesProviderDefaultWhenCommandPathIsEmpty(
        AgentProvider provider,
        string expectedCommandName
    )
    {
        var method = typeof(DoctorService).GetMethod("CheckAgentCommand", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(method).IsNotNull();

        var result = (DoctorCheckResult?)
            method!.Invoke(null, ["Agent", new AgentRoleConfig { Provider = provider, CommandPath = string.Empty }]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Detail).IsNotEqualTo("No command path configured");
        await Assert
            .That(
                Path.GetFileNameWithoutExtension(result.Detail)!
                    .StartsWith(expectedCommandName, StringComparison.OrdinalIgnoreCase)
            )
            .IsTrue();
    }
}
