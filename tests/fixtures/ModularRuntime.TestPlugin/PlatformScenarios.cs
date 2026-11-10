using System.Text.Json.Nodes;
using ModularRuntime.Contracts;

namespace ModularRuntime.TestPlugin;

// Fixture plugins for real AssemblyLoadContext tests of the v0.2 platform. Handlers are
// instance methods on purpose: each registered delegate references the plugin object, so a
// runtime that retained a handler would keep the plugin's load context alive.

/// <summary>Subscribes to <c>test.alc.event</c>. Payload <c>{"mode":"fail"}</c> throws a plugin-defined exception.</summary>
public sealed class EventHandlerScenarioPlugin : IPlugin
{
    private IPluginLogger? _logger;
    private int _received;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;
        context.Events.Subscribe("test.alc.event", OnEventAsync);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private ValueTask OnEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
    {
        _received++;
        _logger?.LogInformation($"EventHandlerScenarioPlugin received event #{_received}.");
        if (pluginEvent.Payload.GetString("mode") == "fail")
        {
            throw new PluginFailureException("intentional event handler failure");
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Provides <c>test.alc.service</c> with operations <c>echo</c>, <c>fail</c> (throws a
/// plugin-defined exception), <c>wait</c> (waits until canceled), and <c>stubborn</c> (ignores
/// cancellation and waits for the host service <c>test.alc.gate</c>).
/// </summary>
public sealed class ServiceProviderScenarioPlugin : IPlugin
{
    private IPluginLogger? _logger;
    private IPluginServiceRegistry? _services;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;
        _services = context.Services;
        context.Services.Register(
            new PluginServiceDefinition("test.alc.service", "ALC test service", new Version(1, 0), ["echo", "fail", "fail-async", "wait", "stubborn"]),
            HandleAsync);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("ServiceProviderScenarioPlugin shutdown.");
        return Task.CompletedTask;
    }

    private async ValueTask<PluginResponse> HandleAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case "echo":
                return PluginResponse.Success(request.Payload);
            case "fail":
                throw new PluginFailureException("intentional service handler failure");
            case "fail-async":
                // Completes asynchronously and faults: the faulted task carries a plugin exception.
                await Task.Yield();
                throw new PluginFailureException("intentional asynchronous service handler failure");
            case "wait":
                _logger?.LogInformation("wait handler entered");
                await _services!.InvokeAsync("test.alc.signal", "entered", cancellationToken: CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return PluginResponse.Success();
                }
                finally
                {
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                    _logger?.LogInformation("wait handler exited");
                }

            case "stubborn":
                // Ignores its cancellation token: waits for the host service test.alc.gate,
                // which completes only when the test releases it.
                _logger?.LogInformation("stubborn handler entered");
                var gate = await _services!.InvokeAsync("test.alc.gate", "wait", cancellationToken: CancellationToken.None).ConfigureAwait(false);
                _logger?.LogInformation($"stubborn handler exited ({gate.Status})");
                return PluginResponse.Success(JsonPayload.FromString("released"));
            default:
                return PluginResponse.Failure("unexpected", request.Operation);
        }
    }
}

/// <summary>
/// Keeps a client for <c>test.alc.service</c> and exposes <c>test.alc.consumer</c>/<c>call</c>, which
/// invokes the retained client and reports the outcome.
/// </summary>
public sealed class ServiceConsumerScenarioPlugin : IPlugin
{
    private IPluginServiceClient? _client;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _client = context.Services.GetClient("test.alc.service");
        context.Services.Register(
            new PluginServiceDefinition("test.alc.consumer", "ALC consumer", new Version(1, 0), ["call"]),
            CallAsync);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async ValueTask<PluginResponse> CallAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            return PluginResponse.Failure("no_client", "The service was not available at startup.");
        }

        var result = await _client.InvokeAsync("echo", request.Payload, cancellationToken).ConfigureAwait(false);
        return PluginResponse.Success(JsonPayload.FromNode(new JsonObject
        {
            ["status"] = result.Status.ToString(),
            ["errorCode"] = result.ErrorCode,
            ["available"] = _client.IsAvailable,
        }));
    }
}

/// <summary>Contributes <c>alc</c> to the host-declared <c>test.alc.point</c>.</summary>
public sealed class ExtensionContributorScenarioPlugin : IPlugin
{
    private int _invocations;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Extensions.Contribute(
            new ExtensionContributionDefinition("alc", "test.alc.point", new Version(1, 0))
            {
                DisplayName = "ALC contributor",
                Metadata = new Dictionary<string, string> { ["kind"] = "test" },
            },
            InvokeAsync);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private ValueTask<PluginResponse> InvokeAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        _invocations++;
        if (request.Operation == "fail")
        {
            throw new PluginFailureException("intentional contribution failure");
        }

        return ValueTask.FromResult(PluginResponse.Success(JsonPayload.FromNode(new JsonObject { ["invocations"] = _invocations })));
    }
}

/// <summary>Creates one resource of every kind and then throws a plugin-defined exception.</summary>
public sealed class ResourceFailureScenarioPlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Events.Subscribe("test.alc.event", OnEventAsync);
        context.Services.Register(
            new PluginServiceDefinition("test.alc.failing-service", "Failing service", new Version(1, 0), ["echo"]),
            HandleAsync);
        context.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition("test.alc.plugin-point", "Plugin point", new Version(1, 0)));
        context.Extensions.Contribute(new ExtensionContributionDefinition("failing", "test.alc.point", new Version(1, 0)), HandleAsync);
        context.Resources.Track("plugin-object", new PluginOwnedDisposable(this));
        throw new PluginFailureException("intentional failure after creating resources");
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private ValueTask OnEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private ValueTask<PluginResponse> HandleAsync(PluginRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginResponse.Success());

    private sealed class PluginOwnedDisposable(ResourceFailureScenarioPlugin owner) : IAsyncDisposable
    {
        public ResourceFailureScenarioPlugin Owner { get; } = owner;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Tracks a resource whose disposal waits for the host service <c>test.alc.gate</c>, and starts
/// disposing it from <c>ShutdownAsync</c> without awaiting it (fire-and-forget cleanup). The
/// runtime must wait for that release before it releases the factory and requests unload.
/// </summary>
public sealed class SlowCleanupScenarioPlugin : IPlugin
{
    private IPluginResource? _handle;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _handle = context.Resources.Track("slow-cleanup", new SlowCleanup(context.Services, context.Logger));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _ = _handle!.DisposeAsync().AsTask();
        return Task.CompletedTask;
    }

    private sealed class SlowCleanup(IPluginServiceRegistry services, IPluginLogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            logger.LogInformation("slow cleanup started");
            await services.InvokeAsync("test.alc.gate", "wait", cancellationToken: CancellationToken.None).ConfigureAwait(false);
            logger.LogInformation("slow cleanup finished");
        }
    }
}

/// <summary>Ignores cancellation in ShutdownAsync (never completes). Used to test the daemon's shutdown timeout policy.</summary>
public sealed class StubbornShutdownPlugin : IPlugin
{
    private IPluginLogger? _logger;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger?.LogWarning("StubbornShutdownPlugin ignores shutdown on purpose.");
        await Task.Delay(Timeout.Infinite, CancellationToken.None).ConfigureAwait(false);
    }
}
