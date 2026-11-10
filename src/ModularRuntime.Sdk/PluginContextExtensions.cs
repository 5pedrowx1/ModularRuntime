using System.Text.Json;
using ModularRuntime.Contracts;

namespace ModularRuntime.Sdk;

/// <summary>The result of a service or contribution call, with the response payload deserialized.</summary>
/// <typeparam name="T">The response type of the operation.</typeparam>
public sealed record PluginCallResult<T>(PluginInvocationResult Result, T? Value)
{
    public bool IsSuccess => Result.IsSuccess;

    public PluginOperationStatus Status => Result.Status;

    public string? ErrorCode => Result.ErrorCode;

    public string? Message => Result.Message;
}

/// <summary>Typed helpers for the event bus.</summary>
public static class PluginEventBusExtensions
{
    /// <summary>Publishes <paramref name="data"/> serialized as JSON on <paramref name="topic"/>.</summary>
    public static ValueTask<EventPublishResult> PublishAsync<T>(
        this IPluginEventBus events,
        string topic,
        T data,
        EventPublishOptions? options = null,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events.PublishAsync(topic, PluginJson.ToPayload(data, jsonOptions), options, cancellationToken);
    }

    /// <summary>
    /// Subscribes to <paramref name="topic"/> with a handler that receives the payload deserialized
    /// as <typeparamref name="T"/>. The subscription is owned by the calling scope and removed
    /// automatically when the plugin stops. A payload that does not match <typeparamref name="T"/>
    /// fails that delivery (the publisher sees it in <see cref="EventPublishResult.Deliveries"/>).
    /// </summary>
    public static IEventSubscription Subscribe<T>(
        this IPluginEventBus events,
        string topic,
        Func<T?, PluginEvent, CancellationToken, ValueTask> handler,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(handler);
        return events.Subscribe(topic, (pluginEvent, cancellationToken) =>
            handler(PluginJson.FromPayload<T>(pluginEvent.Payload, jsonOptions), pluginEvent, cancellationToken));
    }
}

/// <summary>Typed helpers for the service registry.</summary>
public static class PluginServiceRegistryExtensions
{
    /// <summary>
    /// Registers a service whose declared operations are the routes of <paramref name="operations"/>.
    /// The registration is owned by the calling scope and removed automatically when the plugin stops.
    /// </summary>
    public static IServiceRegistration Register(
        this IPluginServiceRegistry services,
        string serviceId,
        string name,
        Version version,
        PluginOperations operations,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(operations);
        var handler = operations.Handler;
        return services.Register(new PluginServiceDefinition(serviceId, name, version, [.. operations.Names]) { Metadata = metadata }, handler);
    }

    /// <summary>
    /// Calls a service operation with a typed request and deserializes a successful response.
    /// A missing or stopped service is a failed result (<see cref="PluginErrorCodes.ServiceNotFound"/>,
    /// <see cref="PluginErrorCodes.ServiceUnavailable"/>), never an exception.
    /// </summary>
    public static async ValueTask<PluginCallResult<TResponse>> InvokeAsync<TRequest, TResponse>(
        this IPluginServiceRegistry services,
        string serviceId,
        string operation,
        TRequest request,
        ServiceInvocationOptions? options = null,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var result = await services.InvokeAsync(serviceId, operation, PluginJson.ToPayload(request, jsonOptions), options, cancellationToken).ConfigureAwait(false);
        return result.ToTyped<TResponse>(jsonOptions);
    }

    /// <summary>Deserializes the payload of a successful invocation result.</summary>
    public static PluginCallResult<T> ToTyped<T>(this PluginInvocationResult result, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsSuccess || result.Payload is null)
        {
            return new PluginCallResult<T>(result, default);
        }

        return PluginJson.TryFromPayload<T>(result.Payload, out var value, out var error, jsonOptions)
            ? new PluginCallResult<T>(result, value)
            : new PluginCallResult<T>(result with { Status = PluginOperationStatus.Failed, ErrorCode = PluginErrorCodes.InvalidResponse, Message = error }, default);
    }
}

/// <summary>Typed helpers for extension points.</summary>
public static class PluginExtensionRegistryExtensions
{
    /// <summary>
    /// Contributes to an extension point with the routes of <paramref name="operations"/>. The
    /// contribution is owned by the calling scope and removed automatically when the plugin stops.
    /// </summary>
    public static IExtensionContribution Contribute(
        this IExtensionRegistry extensions,
        ExtensionContributionDefinition definition,
        PluginOperations operations)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(operations);
        return extensions.Contribute(definition, operations.Handler);
    }

    /// <summary>Calls one contribution with a typed request and deserializes a successful response.</summary>
    public static async ValueTask<PluginCallResult<TResponse>> InvokeAsync<TRequest, TResponse>(
        this IExtensionRegistry extensions,
        string extensionPointId,
        string contributionId,
        string operation,
        TRequest request,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var result = await extensions.InvokeAsync(extensionPointId, contributionId, operation, PluginJson.ToPayload(request, jsonOptions), cancellationToken).ConfigureAwait(false);
        return result.ToTyped<TResponse>(jsonOptions);
    }
}

/// <summary>Helpers for the plugin context.</summary>
public static class PluginContextExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> in the background until the plugin is asked to stop
    /// (<see cref="IPluginContext.StoppingToken"/>). The work is tracked as a resource of the
    /// plugin, so the runtime waits for it to observe cancellation during shutdown. Exceptions
    /// other than cancellation are logged as errors.
    /// </summary>
    public static IPluginResource RunInBackground(this IPluginContext context, string name, Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        var stopping = context.StoppingToken;
        var logger = context.Logger;
        var task = Task.Run(async () =>
        {
            try
            {
                await work(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
#pragma warning disable CA1031 // Background work must not crash the process; the failure is logged.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogError($"Background work '{name}' failed.", exception);
            }
        });
        return context.Resources.Track(name, new BackgroundWork(task));
    }

    private sealed class BackgroundWork(Task task) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await task.ConfigureAwait(false);
    }
}
