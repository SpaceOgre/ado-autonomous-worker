using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Ado.AutonomousWorker.Cli;

public sealed class TypeRegistrar(IServiceProvider serviceProvider) : ITypeRegistrar
{
    public ITypeResolver Build()
    {
        return new TypeResolver(serviceProvider);
    }

    public void Register(Type service, Type implementation)
    {
    }

    public void RegisterInstance(Type service, object implementation)
    {
    }

    public void RegisterLazy(Type service, Func<object> factory)
    {
    }
}
