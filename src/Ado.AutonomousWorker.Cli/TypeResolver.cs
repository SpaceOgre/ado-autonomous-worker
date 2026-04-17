using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli;

public sealed class TypeResolver(IServiceProvider serviceProvider) : ITypeResolver, IDisposable
{
    public object? Resolve(Type? type)
    {
        return type is null ? null : serviceProvider.GetService(type);
    }

    public void Dispose() { }
}
