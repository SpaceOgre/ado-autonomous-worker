namespace Ado.AutonomousWorker.Cli.Interactive;

/// <summary>
/// Describes a slash command available in interactive mode.
/// </summary>
public sealed record SlashCommandDescriptor(
    string Command,
    string Description,
    string Category,
    bool IsBuiltin = false
);
