using Ado.AutonomousWorker.Infrastructure.Agents;

namespace Ado.AutonomousWorker.Tests;

public sealed class ClaudeRunnerTests
{
    [Fact]
    public void ParseEnvelope_ExtractsSummaryAndUsageFromResultLine()
    {
        const string raw = """
            {"type":"system","subtype":"init","session_id":"abc","tools":[]}
            {"type":"assistant","message":{"content":[{"type":"text","text":"Doing work."}]}}
            {"type":"user","message":{"role":"user","content":[]}}
            {"type":"result","subtype":"success","result":"All done.","model":"claude-opus-4-6","total_cost_usd":0.002,"usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10}}
            """;

        var (summary, structuredPayload, usage) = ClaudeRunner.ParseEnvelope(raw, hasSchema: false);

        Assert.Equal("All done.", summary);
        Assert.Null(structuredPayload);
        Assert.NotNull(usage);
        Assert.Equal("claude-opus-4-6", usage.ActualModel);
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(50, usage.OutputTokens);
        Assert.Equal(10, usage.CachedInputTokens);
        Assert.Equal(0.002m, usage.CostUsd);
    }

    [Fact]
    public void ParseEnvelope_ExtractsStructuredPayloadWhenSchemaPresent()
    {
        const string raw = """
            {"type":"system","subtype":"init","session_id":"abc","tools":[]}
            {"type":"result","subtype":"success","result":"Done.","model":"claude-opus-4-6","total_cost_usd":0.001,"structured_output":{"scope":"Backend","confidence":0.9}}
            """;

        var (_, structuredPayload, _) = ClaudeRunner.ParseEnvelope(raw, hasSchema: true);

        Assert.NotNull(structuredPayload);
        Assert.Contains("\"scope\"", structuredPayload);
        Assert.Contains("\"Backend\"", structuredPayload);
    }

    [Fact]
    public void ParseEnvelope_FallsBackToSingleBlobForOldJsonFormat()
    {
        const string raw =
            """{"type":"result","subtype":"success","result":"Legacy result.","model":"claude-opus-4-6","total_cost_usd":0.001}""";

        var (summary, _, usage) = ClaudeRunner.ParseEnvelope(raw, hasSchema: false);

        Assert.Equal("Legacy result.", summary);
        Assert.NotNull(usage);
        Assert.Equal("claude-opus-4-6", usage.ActualModel);
    }

    [Fact]
    public void ParseEnvelope_ReturnsRawWhenNoResultLineExists()
    {
        const string raw = """
            {"type":"system","subtype":"init"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"Working..."}]}}
            """;

        var (summary, structuredPayload, usage) = ClaudeRunner.ParseEnvelope(raw, hasSchema: false);

        Assert.Equal(raw.Trim(), summary);
        Assert.Null(structuredPayload);
        Assert.Null(usage);
    }

    [Fact]
    public void ParseEnvelope_ReturnsEmptyForWhitespaceInput()
    {
        var (summary, structuredPayload, usage) = ClaudeRunner.ParseEnvelope("   ", hasSchema: false);

        Assert.Equal("   ", summary);
        Assert.Null(structuredPayload);
        Assert.Null(usage);
    }
}
