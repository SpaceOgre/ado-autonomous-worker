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
            new ProcessOutputLine(AgentOutputStream.Stdout, "  Implementing validation now.  "));

        Assert.Equal(["Implementing validation now."], result);
    }

    [Fact]
    public void NormalizeClaudeLine_IgnoresJsonWithNoKnownFields()
    {
        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, "{\"scope\":\"Backend\"}"));

        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeClaudeLine_ExtractsMessageFromJsonWithMessageField()
    {
        const string line = """{"message":"Planning repositories and validation strategy."}""";

        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, line));

        Assert.Equal(["Planning repositories and validation strategy."], result);
    }

    [Fact]
    public void NormalizeClaudeLine_ExtractsTextFromAssistantEvent()
    {
        const string line = """
            {"type":"assistant","message":{"content":[{"type":"text","text":"Analyzing the failing test now."}]}}
            """;

        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, line));

        Assert.Equal(["Analyzing the failing test now."], result);
    }

    [Fact]
    public void NormalizeClaudeLine_ExtractsToolNameFromAssistantEvent()
    {
        const string line = """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet test"}}]}}
            """;

        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, line));

        Assert.Equal(["Using tool: Bash"], result);
    }

    [Fact]
    public void NormalizeClaudeLine_ExtractsTextAndToolFromAssistantEvent()
    {
        const string line = """
            {"type":"assistant","message":{"content":[{"type":"text","text":"Running tests."},{"type":"tool_use","name":"Bash","input":{}}]}}
            """;

        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, line));

        Assert.Equal(["Running tests.", "Using tool: Bash"], result);
    }

    [Fact]
    public void NormalizeClaudeLine_SkipsSystemEvent()
    {
        const string line = """{"type":"system","subtype":"init","session_id":"abc","tools":[]}""";

        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, line));

        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeClaudeLine_SkipsResultEvent()
    {
        const string line = """{"type":"result","subtype":"success","result":"Done.","model":"claude-opus-4-5","total_cost_usd":0.001}""";

        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, line));

        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeClaudeLine_SkipsUserEvent()
    {
        const string line = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"ok"}]}}""";

        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, line));

        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeClaudeLine_PrefixesStderrLines()
    {
        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stderr, "API rate limit hit"));

        Assert.Equal(["[stderr] API rate limit hit"], result);
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
