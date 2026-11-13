using System.Text.Json;
using ModularRuntime.Contracts;

namespace ModularRuntime.Sdk;

/// <summary>Helpers for streams (plugin API 0.4).</summary>
public static class PluginStreamExtensions
{
    /// <summary>Writes <paramref name="item"/> serialized as JSON to a <see cref="JsonPayload"/> stream.</summary>
    public static ValueTask<bool> WriteJsonAsync<T>(
        this IStreamProducer<JsonPayload> stream,
        T item,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.WriteAsync(PluginJson.ToPayload(item, jsonOptions), cancellationToken);
    }

    /// <summary>
    /// Subscribes to <paramref name="streamId"/> and reads it in the background until the stream
    /// completes or the plugin stops, calling <paramref name="handler"/> for every message. Both the
    /// subscription and the reader are owned by the plugin instance; the runtime waits for the
    /// reader to observe cancellation during shutdown. Returns <see langword="null"/> when the
    /// stream does not exist (or its version is incompatible), like
    /// <see cref="IEventStreamRegistry.Subscribe{T}"/>.
    /// </summary>
    public static IStreamSubscription<T>? Consume<T>(
        this IPluginContext context,
        string streamId,
        Func<StreamMessage<T>, CancellationToken, ValueTask> handler,
        StreamSubscribeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = context.Streams.Subscribe<T>(streamId, options);
        if (subscription is null)
        {
            return null;
        }

        context.RunInBackground($"stream:{streamId}", async stopping =>
        {
            await foreach (var message in subscription.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                await handler(message, stopping).ConfigureAwait(false);
            }
        });
        return subscription;
    }

    /// <summary>
    /// Like <see cref="Consume{T}"/> for a <see cref="JsonPayload"/> stream, deserializing every item
    /// as <typeparamref name="T"/>; an item that does not match <typeparamref name="T"/> is passed as
    /// <see langword="null"/> together with its raw message.
    /// </summary>
    public static IStreamSubscription<JsonPayload>? ConsumeJson<T>(
        this IPluginContext context,
        string streamId,
        Func<T?, StreamMessage<JsonPayload>, CancellationToken, ValueTask> handler,
        StreamSubscribeOptions? options = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return context.Consume<JsonPayload>(
            streamId,
            (message, cancellationToken) => handler(PluginJson.TryFromPayload<T>(message.Item, out var value, out _, jsonOptions) ? value : default, message, cancellationToken),
            options);
    }
}
