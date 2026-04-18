using Ado.AutonomousWorker.Core.Configuration;

namespace Ado.AutonomousWorker.Infrastructure.Orchestration;

public static class PromptTemplateLoader
{
    public static async Task<string> ApplyAsync(
        AgentRoleConfig config,
        string prompt,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(config.PromptTemplatePath) || !File.Exists(config.PromptTemplatePath))
        {
            return prompt;
        }

        var template = await File.ReadAllTextAsync(config.PromptTemplatePath, cancellationToken);
        return $"{template.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{prompt}";
    }
}
