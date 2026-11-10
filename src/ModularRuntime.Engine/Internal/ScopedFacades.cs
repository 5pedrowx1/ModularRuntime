using ModularRuntime.Contracts;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine.Internal;

/// <summary>
/// Owner-bound view of the platform. Every call made through it is attributed to the owner,
/// and every resource it creates belongs to the owner's <see cref="OwnershipScope"/>.
/// </summary>
internal class RuntimeScopeFacade : IRuntimeScope
{
    public RuntimeScopeFacade(RuntimeCore core, OwnershipScope scope)
    {
        Scope = scope;
        Events = new ScopedEventBus(core, scope);
        Services = new ScopedServiceRegistry(core, scope);
        Extensions = new ScopedExtensionRegistry(core, scope);
        Resources = new ScopedResources(core, scope);
    }

    public ResourceOwner Owner => Scope.Owner;

    public IPluginEventBus Events { get; }

    public IPluginServiceRegistry Services { get; }

    public IExtensionRegistry Extensions { get; }

    public IResourceScope Resources { get; }

    internal OwnershipScope Scope { get; }
}

internal sealed class ScopedEventBus(RuntimeCore core, OwnershipScope scope) : IPluginEventBus
{
    public IEventSubscription Subscribe(string topic, PluginEventCallback handler) =>
        core.Events.Subscribe(scope, topic, handler);

    public ValueTask<EventPublishResult> PublishAsync(
        string topic,
        JsonPayload payload,
        EventPublishOptions? options = null,
        CancellationToken cancellationToken = default) =>
        core.Events.PublishAsync(scope, topic, payload, options, cancellationToken);

    public IReadOnlyList<EventSubscriptionInfo> GetSubscriptions(string? topic = null) =>
        core.Events.GetSubscriptions(topic);
}

internal sealed class ScopedServiceRegistry(RuntimeCore core, OwnershipScope scope) : IPluginServiceRegistry
{
    public IServiceRegistration Register(PluginServiceDefinition definition, PluginInvocationHandler handler) =>
        core.Services.Register(scope, definition, handler);

    public IReadOnlyList<PluginServiceDescriptor> GetServices() => core.Services.GetServices();

    public PluginServiceDescriptor? GetService(string serviceId) => core.Services.GetService(serviceId);

    public IPluginServiceClient? GetClient(string serviceId, Version? minimumVersion = null) =>
        core.Services.GetClient(scope, serviceId, minimumVersion);

    public ValueTask<PluginInvocationResult> InvokeAsync(
        string serviceId,
        string operation,
        JsonPayload? payload = null,
        ServiceInvocationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        core.Services.InvokeAsync(scope, serviceId, operation, payload, options, cancellationToken);
}

internal sealed class ScopedExtensionRegistry(RuntimeCore core, OwnershipScope scope) : IExtensionRegistry
{
    public IExtensionPointRegistration DeclareExtensionPoint(ExtensionPointDefinition definition) =>
        core.Extensions.Declare(scope, definition);

    public IExtensionContribution Contribute(ExtensionContributionDefinition definition, PluginInvocationHandler handler) =>
        core.Extensions.Contribute(scope, definition, handler);

    public IReadOnlyList<ExtensionPointDescriptor> GetExtensionPoints() => core.Extensions.GetExtensionPoints();

    public ExtensionPointDescriptor? GetExtensionPoint(string extensionPointId) =>
        core.Extensions.GetExtensionPoint(extensionPointId);

    public IReadOnlyList<ExtensionContributionDescriptor> GetContributions(string extensionPointId) =>
        core.Extensions.GetContributions(extensionPointId);

    public ValueTask<PluginInvocationResult> InvokeAsync(
        string extensionPointId,
        string contributionId,
        string operation,
        JsonPayload? payload = null,
        CancellationToken cancellationToken = default) =>
        core.Extensions.InvokeAsync(scope, extensionPointId, contributionId, operation, payload, cancellationToken);

    public ValueTask<ExtensionInvocationResult> InvokeAllAsync(
        string extensionPointId,
        string operation,
        JsonPayload? payload = null,
        CancellationToken cancellationToken = default) =>
        core.Extensions.InvokeAllAsync(scope, extensionPointId, operation, payload, cancellationToken);
}

internal sealed class ScopedResources(RuntimeCore core, OwnershipScope scope) : IResourceScope
{
    public ResourceOwner Owner => scope.Owner;

    public bool IsOpen => scope.IsOpen;

    public IPluginResource Track(string name, IAsyncDisposable resource)
    {
        Validation.ValidateDisplayName(name, nameof(name));
        ArgumentNullException.ThrowIfNull(resource);
        core.EnsureCanRegister(scope);

        var tracked = new TrackedResource(scope, name, resource);
        return scope.TryAdd(tracked) ? tracked : throw RuntimeCore.ScopeClosed(scope);
    }

    public IReadOnlyList<ResourceInfo> GetResources() => scope.GetResources();
}
