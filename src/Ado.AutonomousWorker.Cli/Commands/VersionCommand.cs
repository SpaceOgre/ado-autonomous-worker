using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class VersionCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var assemblyName = assembly.GetName();
        var informationalVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assemblyName.Version?.ToString()
            ?? "unknown";

        var table = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumns("Field", "Value");
        table.AddRow("Application", "ado-worker");
        table.AddRow("Version", Markup.Escape(informationalVersion));
        table.AddRow("Assembly Version", assemblyName.Version?.ToString() ?? "unknown");
        table.AddRow("Framework", $".NET {Environment.Version}");

        AnsiConsole.Write(table);
        return 0;
    }
}
