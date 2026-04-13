using Ado.AutonomousWorker.Core.Abstractions;

namespace Ado.AutonomousWorker.Cli.Completion;

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
            return CliCommandCatalog.TopLevelCommands;
        }

        if (IsAwaitingValue(tokens, "--id") || IsAwaitingValue(tokens, "--execution-id"))
        {
            await stateStore.InitializeAsync(cancellationToken);
            return (await stateStore.ListExecutionsAsync(cancellationToken))
                .Select(execution => execution.ExecutionId)
                .ToArray();
        }

        if (CliCommandCatalog.BranchCommands.TryGetValue(tokens[0], out var branchCommands))
        {
            if (tokens.Count == 1)
            {
                return branchCommands;
            }

            if (CliCommandCatalog.CommandOptions.TryGetValue(tokens[0], out var branchOptions)
                && branchOptions.TryGetValue(tokens[1], out var options))
            {
                return options;
            }

            return [];
        }

        if (CliCommandCatalog.CommandOptions.TryGetValue(tokens[0], out var topLevelOptions)
            && topLevelOptions.TryGetValue(string.Empty, out var topLevelSuggestions))
        {
            return topLevelSuggestions;
        }

        return [];
    }

    private static bool IsAwaitingValue(IReadOnlyList<string> tokens, string optionName)
    {
        return tokens.Count > 0 &&
               string.Equals(tokens[^1], optionName, StringComparison.OrdinalIgnoreCase);
    }
}
