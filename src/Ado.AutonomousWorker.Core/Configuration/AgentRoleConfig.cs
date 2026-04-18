using Ado.AutonomousWorker.Core.Enums;

namespace Ado.AutonomousWorker.Core.Configuration;

public sealed class AgentRoleConfig
{
    public AgentProvider Provider { get; set; } = AgentProvider.Codex;

    public string Model { get; set; } = string.Empty;

    public string Effort { get; set; } = "high";

    public string CommandPath { get; set; } = string.Empty;

    public List<string> ExtraArguments { get; set; } = [];

    public string PromptTemplatePath { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 1800;

    public bool RequireStructuredOutput { get; set; } = true;

    public string? ToolPolicy { get; set; }
}
