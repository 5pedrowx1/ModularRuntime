using ModularRuntime.Contracts;

namespace ModularRuntime.Sdk;

/// <summary>
/// Routes the operations of a binary service to handlers and turns the route table into the
/// <see cref="BinaryInvocationHandler"/> the service registry expects; the binary counterpart of
/// <see cref="PluginOperations"/>.
/// </summary>
/// <remarks>
/// Unknown operations are answered with <see cref="PluginErrorCodes.OperationNotSupported"/>; a
/// <see cref="PluginOperationException"/> thrown by a handler with its own code and message. Any
/// other exception propagates to the runtime, which reports <see cref="PluginErrorCodes.HandlerFailed"/>
/// with a diagnostic. The request bytes are borrowed until the handler completes; the response
/// bytes are handed to the caller (see <see cref="BinaryInvocationHandler"/>).
/// </remarks>
public sealed class BinaryOperations
{
    private readonly Dictionary<string, BinaryInvocationHandler> _routes = new(StringComparer.Ordinal);
    private readonly List<string> _names = [];
    private bool _sealed;

    /// <summary>Operation names in registration order.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>The handler to pass to <see cref="IPluginServiceRegistry.RegisterBinary"/>.</summary>
    public BinaryInvocationHandler Handler
    {
        get
        {
            _sealed = true;
            return HandleAsync;
        }
    }

    /// <summary>Adds an operation that receives the raw request and returns the raw response.</summary>
    public BinaryOperations Add(string operation, BinaryInvocationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_sealed)
        {
            throw new InvalidOperationException("Operations cannot be added after the handler was registered.");
        }

        RuntimeName.Validate(operation, nameof(operation));
        if (!_routes.TryAdd(operation, handler))
        {
            throw new ArgumentException($"Operation '{operation}' is already defined.", nameof(operation));
        }

        _names.Add(operation);
        return this;
    }

    /// <summary>Adds an operation that transforms the request bytes into fresh response bytes.</summary>
    public BinaryOperations Add(string operation, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(operation, async (request, cancellationToken) =>
            BinaryResponse.Success(await handler(request.Payload, cancellationToken).ConfigureAwait(false)));
    }

    private async ValueTask<BinaryResponse> HandleAsync(BinaryRequest request, CancellationToken cancellationToken)
    {
        if (!_routes.TryGetValue(request.Operation, out var handler))
        {
            return BinaryResponse.Failure(PluginErrorCodes.OperationNotSupported, $"Operation '{request.Operation}' is not supported.");
        }

        try
        {
            return await handler(request, cancellationToken).ConfigureAwait(false);
        }
        catch (PluginOperationException exception)
        {
            return BinaryResponse.Failure(exception.ErrorCode, exception.Message);
        }
    }
}

/// <summary>Helpers for binary services.</summary>
public static class PluginBinaryServiceExtensions
{
    /// <summary>
    /// Registers a binary service whose declared operations are the routes of <paramref name="operations"/>.
    /// The registration is owned by the calling scope and removed automatically when the plugin stops.
    /// </summary>
    public static IServiceRegistration RegisterBinary(
        this IPluginServiceRegistry services,
        string serviceId,
        string name,
        Version version,
        BinaryOperations operations,
        int? maxRequestBytes = null,
        int? maxResponseBytes = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(operations);
        var handler = operations.Handler;
        return services.RegisterBinary(
            new BinaryServiceDefinition(serviceId, name, version, [.. operations.Names]) { Metadata = metadata, MaxRequestBytes = maxRequestBytes, MaxResponseBytes = maxResponseBytes },
            handler);
    }
}
