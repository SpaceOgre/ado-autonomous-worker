using Ado.AutonomousWorker.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli.Commands;

public sealed class DoctorCommand(DoctorService doctorService) : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var results = await doctorService.RunAsync(cancellationToken);
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Check", "Status", "Detail");

        foreach (var result in results)
        {
            table.AddRow(
                result.Name,
                result.Success ? "[green]OK[/]" : "[red]Fail[/]",
                result.Detail);
        }

        AnsiConsole.Write(table);
        return results.All(result => result.Success) ? 0 : 1;
    }
}
