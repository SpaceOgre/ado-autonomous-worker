using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Agents;
using Ado.AutonomousWorker.Infrastructure.Execution;

namespace Ado.AutonomousWorker.Tests;

public sealed class AgentOutputNormalizerTests
{
    [Fact]
    public void NormalizeClaudeLine_ReturnsReadableStdoutText()
    {
        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, "  Implementing validation now.  "),
            structuredOutput: false);

        Assert.Equal(["Implementing validation now."], result);
    }

    [Fact]
    public void NormalizeClaudeLine_IgnoresStructuredJsonStdout()
    {
        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, "{\"scope\":\"Backend\"}"),
            structuredOutput: true);

        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeCodexLine_ExtractsMessageFromJsonPayload()
    {
        const string line = """{"type":"assistant_message","payload":{"message":"Inspecting failing test and tracing null input path."}}""";

        var result = AgentOutputNormalizer.NormalizeCodexLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, line));

        Assert.Equal(["Inspecting failing test and tracing null input path."], result);
    }

    [Fact]
    public void NormalizeCodexLine_IgnoresSessionMetadata()
    {
        const string line = """{"type":"session_meta","payload":{"id":"abc"}}""";

        var result = AgentOutputNormalizer.NormalizeCodexLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, line));

        Assert.Empty(result);
    }
}
