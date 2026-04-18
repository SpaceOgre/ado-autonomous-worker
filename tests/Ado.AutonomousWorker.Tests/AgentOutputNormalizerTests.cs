using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Infrastructure.Agents;
using Ado.AutonomousWorker.Infrastructure.Execution;

namespace Ado.AutonomousWorker.Tests;

public sealed class AgentOutputNormalizerTests
{
    [Test]
    public async Task NormalizeClaudeLine_ReturnsReadableStdoutText()
    {
        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, "  Implementing validation now.  ")
        );

        await Assert.That(result).IsEquivalentTo(new[] { "Implementing validation now." });
    }

    [Test]
    public async Task NormalizeClaudeLine_IgnoresJsonWithNoKnownFields()
    {
        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stdout, "{\"scope\":\"Backend\"}")
        );

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task NormalizeClaudeLine_ExtractsMessageFromJsonWithMessageField()
    {
        const string line = """{"message":"Planning repositories and validation strategy."}""";

        var result = AgentOutputNormalizer.NormalizeClaudeLine(new ProcessOutputLine(AgentOutputStream.Stdout, line));

        await Assert.That(result).IsEquivalentTo(new[] { "Planning repositories and validation strategy." });
    }

    [Test]
    public async Task NormalizeClaudeLine_ExtractsTextFromAssistantEvent()
    {
        const string line = """
            {"type":"assistant","message":{"content":[{"type":"text","text":"Analyzing the failing test now."}]}}
            """;

        var result = AgentOutputNormalizer.NormalizeClaudeLine(new ProcessOutputLine(AgentOutputStream.Stdout, line));

        await Assert.That(result).IsEquivalentTo(new[] { "Analyzing the failing test now." });
    }

    [Test]
    public async Task NormalizeClaudeLine_ExtractsToolNameFromAssistantEvent()
    {
        const string line = """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet test"}}]}}
            """;

        var result = AgentOutputNormalizer.NormalizeClaudeLine(new ProcessOutputLine(AgentOutputStream.Stdout, line));

        await Assert.That(result).IsEquivalentTo(new[] { "Using tool: Bash" });
    }

    [Test]
    public async Task NormalizeClaudeLine_ExtractsTextAndToolFromAssistantEvent()
    {
        const string line = """
            {"type":"assistant","message":{"content":[{"type":"text","text":"Running tests."},{"type":"tool_use","name":"Bash","input":{}}]}}
            """;

        var result = AgentOutputNormalizer.NormalizeClaudeLine(new ProcessOutputLine(AgentOutputStream.Stdout, line));

        await Assert.That(result).IsEquivalentTo(new[] { "Running tests.", "Using tool: Bash" });
    }

    [Test]
    public async Task NormalizeClaudeLine_SkipsSystemEvent()
    {
        const string line = """{"type":"system","subtype":"init","session_id":"abc","tools":[]}""";

        var result = AgentOutputNormalizer.NormalizeClaudeLine(new ProcessOutputLine(AgentOutputStream.Stdout, line));

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task NormalizeClaudeLine_SkipsResultEvent()
    {
        const string line =
            """{"type":"result","subtype":"success","result":"Done.","model":"claude-opus-4-5","total_cost_usd":0.001}""";

        var result = AgentOutputNormalizer.NormalizeClaudeLine(new ProcessOutputLine(AgentOutputStream.Stdout, line));

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task NormalizeClaudeLine_SkipsUserEvent()
    {
        const string line =
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"ok"}]}}""";

        var result = AgentOutputNormalizer.NormalizeClaudeLine(new ProcessOutputLine(AgentOutputStream.Stdout, line));

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task NormalizeClaudeLine_PrefixesStderrLines()
    {
        var result = AgentOutputNormalizer.NormalizeClaudeLine(
            new ProcessOutputLine(AgentOutputStream.Stderr, "API rate limit hit")
        );

        await Assert.That(result).IsEquivalentTo(new[] { "[stderr] API rate limit hit" });
    }

    [Test]
    public async Task NormalizeCodexLine_ExtractsMessageFromJsonPayload()
    {
        const string line =
            """{"type":"assistant_message","payload":{"message":"Inspecting failing test and tracing null input path."}}""";

        var result = AgentOutputNormalizer.NormalizeCodexLine(new ProcessOutputLine(AgentOutputStream.Stdout, line));

        await Assert.That(result).IsEquivalentTo(new[] { "Inspecting failing test and tracing null input path." });
    }

    [Test]
    public async Task NormalizeCodexLine_IgnoresSessionMetadata()
    {
        const string line = """{"type":"session_meta","payload":{"id":"abc"}}""";

        var result = AgentOutputNormalizer.NormalizeCodexLine(new ProcessOutputLine(AgentOutputStream.Stdout, line));

        await Assert.That(result).IsEmpty();
    }
}
