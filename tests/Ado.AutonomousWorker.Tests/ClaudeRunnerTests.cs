using Ado.AutonomousWorker.Infrastructure.Agents;

namespace Ado.AutonomousWorker.Tests;

public sealed class ClaudeRunnerTests
{
    [Test]
    public async Task ParseEnvelope_ExtractsSummaryAndUsageFromResultLine()
    {
        const string raw = """
            {"type":"system","subtype":"init","session_id":"abc","tools":[]}
            {"type":"assistant","message":{"content":[{"type":"text","text":"Doing work."}]}}
            {"type":"user","message":{"role":"user","content":[]}}
            {"type":"result","subtype":"success","result":"All done.","model":"claude-opus-4-6","total_cost_usd":0.002,"usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10}}
            """;

        var (summary, structuredPayload, usage) = ClaudeRunner.ParseEnvelope(raw, hasSchema: false);

        await Assert.That(summary).IsEqualTo("All done.");
        await Assert.That(structuredPayload).IsNull();
        await Assert.That(usage).IsNotNull();
        await Assert.That(usage!.ActualModel).IsEqualTo("claude-opus-4-6");
        await Assert.That(usage.InputTokens).IsEqualTo(100);
        await Assert.That(usage.OutputTokens).IsEqualTo(50);
        await Assert.That(usage.CachedInputTokens).IsEqualTo(10);
        await Assert.That(usage.CostUsd).IsEqualTo(0.002m);
    }

    [Test]
    public async Task ParseEnvelope_ExtractsStructuredPayloadWhenSchemaPresent()
    {
        const string raw = """
            {"type":"system","subtype":"init","session_id":"abc","tools":[]}
            {"type":"result","subtype":"success","result":"Done.","model":"claude-opus-4-6","total_cost_usd":0.001,"structured_output":{"scope":"Backend","confidence":0.9}}
            """;

        var (_, structuredPayload, _) = ClaudeRunner.ParseEnvelope(raw, hasSchema: true);

        await Assert.That(structuredPayload).IsNotNull();
        await Assert.That(structuredPayload).Contains("\"scope\"");
        await Assert.That(structuredPayload).Contains("\"Backend\"");
    }

    [Test]
    public async Task ParseEnvelope_FallsBackToSingleBlobForOldJsonFormat()
    {
        const string raw =
            """{"type":"result","subtype":"success","result":"Legacy result.","model":"claude-opus-4-6","total_cost_usd":0.001}""";

        var (summary, _, usage) = ClaudeRunner.ParseEnvelope(raw, hasSchema: false);

        await Assert.That(summary).IsEqualTo("Legacy result.");
        await Assert.That(usage).IsNotNull();
        await Assert.That(usage!.ActualModel).IsEqualTo("claude-opus-4-6");
    }

    [Test]
    public async Task ParseEnvelope_ReturnsRawWhenNoResultLineExists()
    {
        const string raw = """
            {"type":"system","subtype":"init"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"Working..."}]}}
            """;

        var (summary, structuredPayload, usage) = ClaudeRunner.ParseEnvelope(raw, hasSchema: false);

        await Assert.That(summary).IsEqualTo(raw.Trim());
        await Assert.That(structuredPayload).IsNull();
        await Assert.That(usage).IsNull();
    }

    [Test]
    public async Task ParseEnvelope_ReturnsEmptyForWhitespaceInput()
    {
        var (summary, structuredPayload, usage) = ClaudeRunner.ParseEnvelope("   ", hasSchema: false);

        await Assert.That(summary).IsEqualTo("   ");
        await Assert.That(structuredPayload).IsNull();
        await Assert.That(usage).IsNull();
    }
}
