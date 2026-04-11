using Ado.AutonomousWorker.Core.Services;

namespace Ado.AutonomousWorker.Cli.Services;

public sealed class CliCompletionService(IStateStore stateStore)
{
    public async Task<IReadOnlyList<string>> GetSuggestionsAsync(string currentWord, IReadOnlyList<string> tokens, CancellationToken cancellationToken)
    {
        var suggestions = await GetUnfilteredSuggestionsAsync(tokens, cancellationToken);
        return suggestions
            .Where(suggestion => suggestion.StartsWith(currentWord ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(suggestion => suggestion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string BuildPowerShellScript(string commandName)
    {
        return $$"""
        Register-ArgumentCompleter -Native -CommandName '{{commandName}}' -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)

            $tokens = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.Extent.Text })
            if ($wordToComplete -and $tokens.Count -gt 0 -and $tokens[-1] -eq $wordToComplete) {
                if ($tokens.Count -eq 1) {
                    $tokens = @()
                }
                else {
                    $tokens = $tokens[0..($tokens.Count - 2)]
                }
            }

            $completionArgs = @('__complete', '--current', $wordToComplete, '--') + $tokens
            & '{{commandName}}' @completionArgs | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }
        """;
    }

    private async Task<IReadOnlyList<string>> GetUnfilteredSuggestionsAsync(IReadOnlyList<string> tokens, CancellationToken cancellationToken)
    {
        if (tokens.Count == 0)
        {
            return ["bootstrap", "completion", "doctor", "execution", "executions", "queue", "run", "task"];
        }

        if (IsAwaitingValue(tokens, "--id") || IsAwaitingValue(tokens, "--execution-id"))
        {
            await stateStore.InitializeAsync(cancellationToken);
            return (await stateStore.ListExecutionsAsync(cancellationToken))
                .Select(execution => execution.ExecutionId)
                .ToArray();
        }

        return tokens[0] switch
        {
            "completion" => SuggestForCompletion(tokens),
            "execution" => SuggestForExecution(tokens),
            "executions" => SuggestForExecutions(tokens),
            "task" => SuggestForTask(tokens),
            _ => [],
        };
    }

    private static IReadOnlyList<string> SuggestForCompletion(IReadOnlyList<string> tokens)
    {
        return tokens.Count switch
        {
            1 => ["powershell"],
            _ => [],
        };
    }

    private static IReadOnlyList<string> SuggestForExecution(IReadOnlyList<string> tokens)
    {
        return tokens.Count switch
        {
            1 => ["show"],
            2 when tokens[1] == "show" => ["--id", "--last"],
            _ => [],
        };
    }

    private static IReadOnlyList<string> SuggestForExecutions(IReadOnlyList<string> tokens)
    {
        return tokens.Count switch
        {
            1 => ["--active"],
            _ => [],
        };
    }

    private static IReadOnlyList<string> SuggestForTask(IReadOnlyList<string> tokens)
    {
        return tokens.Count switch
        {
            1 => ["resume", "run"],
            2 when tokens[1] == "run" => ["--id"],
            2 when tokens[1] == "resume" => ["--execution-id"],
            _ => [],
        };
    }

    private static bool IsAwaitingValue(IReadOnlyList<string> tokens, string optionName)
    {
        return tokens.Count > 0 &&
               string.Equals(tokens[^1], optionName, StringComparison.OrdinalIgnoreCase);
    }
}
