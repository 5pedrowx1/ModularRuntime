using ModularRuntime.Contracts;
using ModularRuntime.TestPlugin.Dependency;

namespace ModularRuntime.TestPlugin;

/// <summary>Loader fixture with a private managed dependency and shared contracts access.</summary>
public sealed class TestPlugin : IPlugin
{
    private readonly string _dependencyMessage = DependencyMessage.GetMessage();
    private readonly Type _contractsType = typeof(IPlugin);

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("TestPlugin initialized.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public string GetDependencyMessage() => _dependencyMessage;

    public Type ContractsType => _contractsType;
}
