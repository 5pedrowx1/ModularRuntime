using ModularRuntime.Contracts;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine.Internal;

internal sealed class EnginePluginContext : RuntimeScopeFacade, IPluginContext
{
    public EnginePluginContext(
        RuntimeCore core,
        OwnershipScope scope,
        PluginDescriptor descriptor,
        Guid instanceId,
        IPluginLogger logger,
        CancellationToken stoppingToken)
        : base(core, scope)
    {
        PluginId = descriptor.Id;
        Descriptor = descriptor;
        InstanceId = instanceId;
        Logger = logger;
        StoppingToken = stoppingToken;
    }

    public Guid InstanceId { get; }

    public PluginId PluginId { get; }

    public PluginDescriptor Descriptor { get; }

    public IPluginLogger Logger { get; }

    public CancellationToken StoppingToken { get; }
}
