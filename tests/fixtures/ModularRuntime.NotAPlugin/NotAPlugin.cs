using ModularRuntime.Contracts;

namespace ModularRuntime.NotAPlugin;

/// <summary>Fixture type that deliberately does not implement <see cref="IPlugin"/>.</summary>
public sealed class NotAPlugin
{
    private readonly Type _contractsType = typeof(IPlugin);

    public Type ContractsType => _contractsType;
}
