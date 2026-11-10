using System.Text.Json;
using ModularRuntime.Contracts;

namespace ModularRuntime.Sdk;

/// <summary>
/// Routes the operations of a service or an extension contribution to typed handlers, and turns
/// the route table into the <see cref="PluginInvocationHandler"/> the registries expect.
/// </summary>
/// <remarks>
/// <para>
/// Unknown operations are answered with <see cref="PluginErrorCodes.OperationNotSupported"/>;
/// a request payload that does not match the handler's request type with
/// <see cref="PluginErrorCodes.InvalidPayload"/>; a <see cref="PluginOperationException"/>
/// thrown by a handler with its own code and message. Any other exception propagates to the
/// runtime, which reports <see cref="PluginErrorCodes.HandlerFailed"/> with a diagnostic.
/// </para>
/// <para>
/// Register the routes before registering the service: the registry receives
/// <see cref="Names"/> as the declared operations. The router is immutable once
/// <see cref="Handler"/> has been read.
/// </para>
/// </remarks>
public sealed class PluginOperations
{
    private readonly Dictionary<string, PluginInvocationHandler> _routes = new(StringComparer.Ordinal);
    private readonly List<string> _names = [];
    private readonly JsonSerializerOptions _json;
    private bool _sealed;

    public PluginOperations(JsonSerializerOptions? jsonOptions = null)
    {
        _json = jsonOptions ?? PluginJson.DefaultOptions;
    }

    /// <summary>Operation names in registration order.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>The handler to pass to the service registry or the extension registry.</summary>
    public PluginInvocationHandler Handler
    {
        get
        {
            _sealed = true;
            return HandleAsync;
        }
    }

    /// <summary>Adds an operation that receives the raw request and returns the raw response.</summary>
    public PluginOperations AddHandler(string operation, PluginInvocationHandler handler)
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

    /// <summary>Adds an operation with a typed request and a typed response.</summary>
    public PluginOperations Add<TRequest, TResponse>(
        string operation,
        Func<TRequest, PluginRequest, CancellationToken, ValueTask<TResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return AddHandler(operation, async (request, cancellationToken) =>
        {
            if (!PluginJson.TryFromPayload<TRequest>(request.Payload, out var value, out var error, _json) || value is null)
            {
                return PluginResponse.Failure(PluginErrorCodes.InvalidPayload, error ?? $"Operation '{operation}' requires a {typeof(TRequest).Name}.");
            }

            var response = await handler(value, request, cancellationToken).ConfigureAwait(false);
            return PluginResponse.Success(PluginJson.ToPayload(response, _json));
        });
    }

    /// <summary>Adds an operation without request data and with a typed response.</summary>
    public PluginOperations Add<TResponse>(
        string operation,
        Func<PluginRequest, CancellationToken, ValueTask<TResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return AddHandler(operation, async (request, cancellationToken) =>
            PluginResponse.Success(PluginJson.ToPayload(await handler(request, cancellationToken).ConfigureAwait(false), _json)));
    }

    private async ValueTask<PluginResponse> HandleAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        if (!_routes.TryGetValue(request.Operation, out var handler))
        {
            return PluginResponse.Failure(PluginErrorCodes.OperationNotSupported, $"Operation '{request.Operation}' is not supported.");
        }

        try
        {
            return await handler(request, cancellationToken).ConfigureAwait(false);
        }
        catch (PluginOperationException exception)
        {
            return PluginResponse.Failure(exception.ErrorCode, exception.Message);
        }
    }
}

/// <summary>
/// Thrown by an operation handler to answer with an expected, documented failure (for example
/// "not_found"); the caller receives the code and message. Other exceptions are reported by the
/// runtime as <see cref="PluginErrorCodes.HandlerFailed"/>.
/// </summary>
public sealed class PluginOperationException : Exception
{
    public PluginOperationException()
        : this("operation_failed", "The operation failed.")
    {
    }

    public PluginOperationException(string message)
        : this("operation_failed", message)
    {
    }

    public PluginOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = "operation_failed";
    }

    public PluginOperationException(string errorCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
