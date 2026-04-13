using System.ComponentModel;
using System.Diagnostics;
using Ado.AutonomousWorker.Cli.Presentation;
using Ado.AutonomousWorker.Core.Abstractions;
using Ado.AutonomousWorker.Core.Configuration;
using Ado.AutonomousWorker.Core.Enums;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class AgentTestCommand(
    IAgentRunnerFactory runnerFactory,
    IOptions<AgentOptions> agentOptions) : AsyncCommand<AgentTestCommand.Settings>
{
    private const string TestPrompt =
        "This is a connectivity test from the ado-worker CLI. Do not take any real actions. Reply with exactly the word: OK";

    public sealed class Settings : CommandSettings
    {
        [Description("Agent role to test: plan, code, or review. If omitted, all three roles are tested.")]
        [CommandOption("--role <ROLE>")]
        public string? Role { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var options = agentOptions.Value;
        (string Label, AgentRole Role, AgentRoleConfig Config)[] candidates =
        [
            ("Plan",   AgentRole.Plan,   options.Plan),
            ("Code",   AgentRole.Code,   options.Code),
            ("Review", AgentRole.Review, options.Review),
            ("Pr",     AgentRole.Plan,   options.Pr),
        ];

        var targets = string.IsNullOrWhiteSpace(settings.Role)
            ? candidates
            : candidates
                .Where(c => string.Equals(c.Label, settings.Role, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (targets.Length == 0)
        {
            AnsiConsole.MarkupLine($"[red]Unknown role '{Markup.Escape(settings.Role!)}'. Valid values: plan, code, review, pr.[/]");
            return 1;
        }

        var allPassed = true;
        foreach (var (label, role, config) in targets)
        {
            if (!await TestRoleAsync(label, role, config, cancellationToken))
                allPassed = false;
        }

        return allPassed ? 0 : 1;
    }

    private async Task<bool> TestRoleAsync(string label, AgentRole role, AgentRoleConfig config, CancellationToken cancellationToken)
    {
        AnsiConsole.Markup(
            $"Testing [cornflowerblue]{Markup.Escape(label)}[/] {CliStyle.Provider(config.Provider)} " +
            $"[grey]({Markup.Escape(config.Model)})[/] ... ");

        var artifactDir = Path.Combine(Path.GetTempPath(), "ado-worker-agent-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);
        var sw = Stopwatch.StartNew();
        var passed = false;

        try
        {
            var runner = runnerFactory.GetRunner(config.Provider);
            var testConfig = CreateTestConfig(config);
            var result = await runner.StartSessionAsync(
                role,
                testConfig,
                TestPrompt,
                Directory.GetCurrentDirectory(),
                artifactDir,
                outputSchema: null,
                outputCallback: null,
                cancellationToken);

            sw.Stop();
            var elapsed = FormatElapsed(sw.Elapsed);
            var response = result.Result.Summary.Trim();
            var preview = response.Length > 80 ? response[..80] + "…" : response;

            if (result.Result.Success)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] [grey]{Markup.Escape(preview)}  (elapsed {elapsed})[/]");
                passed = true;
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]✗[/] [grey]{Markup.Escape(preview)}  (elapsed {elapsed})[/]");
                AnsiConsole.MarkupLine($"  [grey]Transcript: {Markup.Escape(result.Result.TranscriptPath)}[/]");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/] [grey](elapsed {FormatElapsed(sw.Elapsed)})[/]");
        }
        finally
        {
            if (passed)
            {
                try
                {
                    Directory.Delete(artifactDir, recursive: true);
                }
                catch { /* best-effort cleanup */ }
            }
        }

        return passed;
    }

    private static AgentRoleConfig CreateTestConfig(AgentRoleConfig baseConfig) => new()
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

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");
}
