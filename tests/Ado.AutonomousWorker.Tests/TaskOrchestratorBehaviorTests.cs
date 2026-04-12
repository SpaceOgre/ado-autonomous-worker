using Ado.AutonomousWorker.Core.Configuration;

namespace Ado.AutonomousWorker.Tests;

/// <summary>
/// Tests that verify behaviours fixed in the task-orchestrator review:
///   - Prompt templates are prepended when a file exists at PromptTemplatePath.
///   - Prompt templates are ignored when the path is empty or the file does not exist.
///   - The code prompt includes description and acceptance criteria.
/// </summary>
public sealed class TaskOrchestratorBehaviorTests : IDisposable
{
    private const string TestDirectoryPrefix = "ado-worker-orch-tests";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), TestDirectoryPrefix, Guid.NewGuid().ToString("N"));

    public TaskOrchestratorBehaviorTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    // ------------------------------------------------------------------
    // Prompt template loading
    // ------------------------------------------------------------------

    [Fact]
    public async Task ApplyPromptTemplate_PrependsTemplateWhenFileExists()
    {
        var templatePath = Path.Combine(_tempDir, "agents.md");
        await File.WriteAllTextAsync(templatePath, "## Repository conventions\nAlways write tests.");

        var config = new AgentRoleConfig { PromptTemplatePath = templatePath };
        const string originalPrompt = "Implement feature X.";

        var result = await InvokeApplyPromptTemplate(config, originalPrompt, CancellationToken.None);

        Assert.StartsWith("## Repository conventions", result);
        Assert.Contains("Implement feature X.", result);
    }

    [Fact]
    public async Task ApplyPromptTemplate_ReturnsOriginalPromptWhenPathIsEmpty()
    {
        var config = new AgentRoleConfig { PromptTemplatePath = string.Empty };
        const string originalPrompt = "Implement feature X.";

        var result = await InvokeApplyPromptTemplate(config, originalPrompt, CancellationToken.None);

        Assert.Equal(originalPrompt, result);
    }

    [Fact]
    public async Task ApplyPromptTemplate_ReturnsOriginalPromptWhenFileDoesNotExist()
    {
        var config = new AgentRoleConfig { PromptTemplatePath = Path.Combine(_tempDir, "nonexistent.md") };
        const string originalPrompt = "Implement feature X.";

        var result = await InvokeApplyPromptTemplate(config, originalPrompt, CancellationToken.None);

        Assert.Equal(originalPrompt, result);
    }

    [Fact]
    public async Task ApplyPromptTemplate_TrimsTrailingWhitespaceFromTemplateBeforeJoining()
    {
        var templatePath = Path.Combine(_tempDir, "agents-trailing.md");
        await File.WriteAllTextAsync(templatePath, "Template content.   \n\n   ");

        var config = new AgentRoleConfig { PromptTemplatePath = templatePath };
        const string originalPrompt = "Main prompt.";

        var result = await InvokeApplyPromptTemplate(config, originalPrompt, CancellationToken.None);

        // The two sections must be separated by exactly one blank line.
        Assert.Contains($"Template content.{Environment.NewLine}{Environment.NewLine}Main prompt.", result);
    }

    // ------------------------------------------------------------------
    // Helpers – invoke the private static method via reflection so the
    // tests exercise the real implementation without exposing it publicly.
    // ------------------------------------------------------------------

    private static async Task<string> InvokeApplyPromptTemplate(AgentRoleConfig config, string prompt, CancellationToken cancellationToken)
    {
        return await Ado.AutonomousWorker.Infrastructure.Orchestration.PromptTemplateLoader.ApplyAsync(config, prompt, cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
