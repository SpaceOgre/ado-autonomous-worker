using System.ComponentModel;
using System.Diagnostics;
using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Ado.AutonomousWorker.Core.Models;
using Ado.AutonomousWorker.Infrastructure.Execution;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class AgentTestCommand(IAgentRunnerFactory runnerFactory, IOptions<AgentOptions> agentOptions)
    : AsyncCommand<AgentTestCommand.Settings>
{
    private const string TestPrompt =
        "This is a connectivity test from the ado-worker CLI. Do not take any real actions. Reply with exactly the word: OK";

    private const string StructuredTestPrompt =
        "This is a connectivity test from the ado-worker CLI. Do not take any real actions. "
        + "Return a minimal valid JSON object that matches the provided schema: "
        + "use \"test\" for string fields, empty arrays for array fields, and null for nullable fields.";

    public sealed class Settings : CommandSettings
    {
        [Description("Agent role to test: plan, code, or review. If omitted, all three roles are tested.")]
        [CommandOption("--role <ROLE>")]
        public string? Role { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var options = agentOptions.Value;
        (string Label, AgentRole Role, AgentRoleConfig Config, string? Schema)[] candidates =
        [
            ("Plan", AgentRole.Plan, options.Plan, AgentOutputSchemas.Plan),
            ("Code", AgentRole.Code, options.Code, null),
            ("Review", AgentRole.Review, options.Review, AgentOutputSchemas.Review),
            ("Pr", AgentRole.Plan, options.Pr, AgentOutputSchemas.PullRequestDescription),
        ];

        var targets = string.IsNullOrWhiteSpace(settings.Role)
            ? candidates
            : candidates
                .Where(c => string.Equals(c.Label, settings.Role, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (targets.Length == 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]Unknown role '{Markup.Escape(settings.Role!)}'. Valid values: plan, code, review, pr.[/]"
            );
            return 1;
        }

        var allPassed = true;
        foreach (var (label, role, config, schema) in targets)
        {
            if (!await TestRoleAsync(label, role, config, schema, cancellationToken))
                allPassed = false;
        }

        return allPassed ? 0 : 1;
    }

    private async Task<bool> TestRoleAsync(
        string label,
        AgentRole role,
        AgentRoleConfig config,
        string? schema,
        CancellationToken cancellationToken
    )
    {
        var schemaLabel = schema is not null ? " [grey](structured)[/]" : string.Empty;
        AnsiConsole.Markup(
            $"Testing [cornflowerblue]{Markup.Escape(label)}[/]{schemaLabel} {CliStyle.Provider(config.Provider)} "
                + $"[grey]({Markup.Escape(config.Model)})[/] ... "
        );

        var artifactDir = Path.Combine(Path.GetTempPath(), "ado-worker-agent-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);
        var sw = Stopwatch.StartNew();
        var passed = false;

        try
        {
            var runner = runnerFactory.GetRunner(config.Provider);
            var testConfig = CreateTestConfig(config);
            var prompt = schema is not null ? StructuredTestPrompt : TestPrompt;
            var result = await runner.StartSessionAsync(
                role,
                testConfig,
                prompt,
                Directory.GetCurrentDirectory(),
                artifactDir,
                outputSchema: schema,
                outputCallback: null,
                cancellationToken
            );

            sw.Stop();
            var elapsed = FormatElapsed(sw.Elapsed);
            var preview = result.Result.Summary.Length > 60 ? result.Result.Summary[..60] + "…" : result.Result.Summary;
            var metaSuffix = FormatUsageSuffix(result.Result.Usage);

            if (result.Result.Success)
            {
                AnsiConsole.MarkupLine(
                    $"[green]✓[/] [grey]{Markup.Escape(preview)}{Markup.Escape(metaSuffix)}  (elapsed {elapsed})[/]"
                );
                passed = true;
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[red]✗[/] [grey]{Markup.Escape(preview)}{Markup.Escape(metaSuffix)}  (elapsed {elapsed})[/]"
                );
                AnsiConsole.MarkupLine($"  [grey]Transcript: {Markup.Escape(result.Result.TranscriptPath)}[/]");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            AnsiConsole.MarkupLine(
                $"[red]✗ {Markup.Escape(ex.Message)}[/] [grey](elapsed {FormatElapsed(sw.Elapsed)})[/]"
            );
        }
        finally
        {
            if (passed)
            {
                try
                {
                    Directory.Delete(artifactDir, recursive: true);
                }
                catch
                { /* best-effort cleanup */
                }
            }
        }

        return passed;
    }

    private static AgentRoleConfig CreateTestConfig(AgentRoleConfig baseConfig) =>
        new()
        {
            Provider = baseConfig.Provider,
            Model = baseConfig.Model,
            Effort = "low",
            CommandPath = baseConfig.CommandPath,
            ExtraArguments = baseConfig.ExtraArguments,
            TimeoutSeconds = 120,
            RequireStructuredOutput = false,
            ToolPolicy = baseConfig.ToolPolicy,
        };

    // Format the usage metadata suffix for display.
    // Claude: model + tokens + cost. Codex: tokens only (no model or cost available from CLI).
    private static string FormatUsageSuffix(AgentUsage? usage)
    {
        if (usage is null)
            return string.Empty;

        var parts = new List<string>();
        if (usage.ActualModel is not null)
            parts.Add(usage.ActualModel);

        if (usage.InputTokens > 0 || usage.OutputTokens > 0)
        {
            var tokens = $"{usage.InputTokens} in / {usage.OutputTokens} out";
            if (usage.CachedInputTokens > 0)
                tokens += $" ({usage.CachedInputTokens} cached)";
            parts.Add(tokens);
        }

        if (usage.CostUsd.HasValue)
            parts.Add($"${usage.CostUsd:F4}");

        return parts.Count > 0 ? "  · " + string.Join(" · ", parts) : string.Empty;
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");
}
