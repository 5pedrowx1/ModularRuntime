using ModularRuntime.Contracts;
using ModularRuntime.Engine;

namespace ModularRuntime.Sdk.Tests;

public sealed record Greeting(string Name, int Count);

public sealed record GreetingReply(string Text);

/// <summary>The SDK helpers against the real engine (in-process plugins, real registries).</summary>
public sealed class SdkTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly PluginEngine _engine = new();

    public async ValueTask DisposeAsync() => await _engine.DisposeAsync();

    private async Task<IPluginContext> StartAsync(string id, Func<IPluginContext, Task>? initialize = null)
    {
        IPluginContext? captured = null;
        var descriptor = new PluginDescriptor(PluginId.Parse(id), $"Plugin {id}", new Version(2, 1, 0), PluginApiVersion.Supported, "P.dll", "P.Plugin");
        var plugin = new DelegatePlugin(async context =>
        {
            captured = context;
            if (initialize is not null)
            {
                await initialize(context);
            }
        });
        Assert.True(_engine.Register(descriptor, new DelegateFactory(plugin)).IsSuccess);
        Assert.True(_engine.MarkValidated(descriptor.Id).IsSuccess);
        var result = await _engine.StartAsync(descriptor.Id).WaitAsync(Timeout);
        Assert.True(result.IsSuccess, result.Message);
        return captured!;
    }

    [Fact]
    public void ContractsAssemblyVersion_FollowsThePluginApiVersion()
    {
        var version = typeof(IPlugin).Assembly.GetName().Version!;
        Assert.Equal(PluginApiVersion.Supported.Major, version.Major);
        Assert.Equal(PluginApiVersion.Supported.Minor, version.Minor);
    }

    [Fact]
    public async Task Context_ExposesTheValidatedDescriptor()
    {
        var context = await StartAsync("test.identity");

        Assert.Equal("test.identity", context.PluginId.Value);
        Assert.Equal(context.PluginId, context.Descriptor.Id);
        Assert.Equal("Plugin test.identity", context.Descriptor.Name);
        Assert.Equal(new Version(2, 1, 0), context.Descriptor.Version);
    }

    [Fact]
    public void Payloads_RoundTripAsCamelCaseJson()
    {
        var payload = PluginJson.ToPayload(new Greeting("Ada", 2));

        Assert.Equal("""{"name":"Ada","count":2}""", payload.Json);
        Assert.Equal(new Greeting("Ada", 2), payload.As<Greeting>());
        Assert.False(PluginJson.TryFromPayload<Greeting>(JsonPayload.Parse("""{"name":5}"""), out _, out var error));
        Assert.Contains(nameof(Greeting), error, StringComparison.Ordinal);
        Assert.Throws<PluginPayloadException>(() => JsonPayload.Parse("[1]").As<Greeting>());
    }

    [Fact]
    public async Task Events_TypedPublishAndSubscribe_AndSubscriptionsAreOwned()
    {
        var received = new TaskCompletionSource<(Greeting? Data, string Source)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = await StartAsync("test.subscriber", context =>
        {
            context.Events.Subscribe<Greeting>("test.greetings", (data, pluginEvent, _) =>
            {
                received.TrySetResult((data, pluginEvent.Source.PluginId!.Value.Value));
                return ValueTask.CompletedTask;
            });
            return Task.CompletedTask;
        });
        var publisher = await StartAsync("test.publisher");

        var result = await publisher.Events.PublishAsync("test.greetings", new Greeting("Ada", 1));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.SucceededCount);
        var (data, source) = await received.Task.WaitAsync(Timeout);
        Assert.Equal(new Greeting("Ada", 1), data);
        Assert.Equal("test.publisher", source);
        Assert.Contains(subscriber.Resources.GetResources(), r => r.Kind == ResourceKind.EventSubscription);

        Assert.True((await _engine.StopAsync(PluginId.Parse("test.subscriber"))).IsSuccess);
        Assert.Empty(_engine.Host.Events.GetSubscriptions("test.greetings"));
    }

    [Fact]
    public async Task Events_PayloadOfTheWrongShape_FailsThatDeliveryOnly()
    {
        await StartAsync("test.subscriber", context =>
        {
            context.Events.Subscribe<Greeting>("test.greetings", (_, _, _) => ValueTask.CompletedTask);
            return Task.CompletedTask;
        });

        var result = await _engine.Host.Events.PublishAsync("test.greetings", JsonPayload.Parse("[]"));

        Assert.Equal(1, result.FailedCount);
    }

    [Fact]
    public async Task Services_TypedOperations_AreDeclaredRoutedAndInvoked()
    {
        await StartAsync("test.provider", context =>
        {
            var operations = new PluginOperations()
                .Add<Greeting, GreetingReply>("greet", (greeting, _, _) => ValueTask.FromResult(new GreetingReply($"Hello, {greeting.Name} x{greeting.Count}")))
                .Add<GreetingReply>("ping", (_, _) => ValueTask.FromResult(new GreetingReply("pong")))
                .Add<Greeting, GreetingReply>("fail", (_, _, _) => throw new PluginOperationException("not_found", "No such greeting."));
            context.Services.Register("test.greeter", "Greeter", new Version(1, 2), operations);
            return Task.CompletedTask;
        });
        var consumer = await StartAsync("test.consumer");

        var descriptor = consumer.Services.GetService("test.greeter");
        Assert.Equal(["greet", "ping", "fail"], descriptor!.Operations);

        var greet = await consumer.Services.InvokeAsync<Greeting, GreetingReply>("test.greeter", "greet", new Greeting("Ada", 3));
        Assert.True(greet.IsSuccess, greet.Message);
        Assert.Equal("Hello, Ada x3", greet.Value!.Text);

        var invalid = await consumer.Services.InvokeAsync<int[], GreetingReply>("test.greeter", "greet", [1]);
        Assert.Equal(PluginErrorCodes.InvalidPayload, invalid.ErrorCode);

        var unsupported = await consumer.Services.InvokeAsync<Greeting, GreetingReply>("test.greeter", "unknown", new Greeting("x", 1));
        Assert.False(unsupported.IsSuccess);

        var failed = await consumer.Services.InvokeAsync<Greeting, GreetingReply>("test.greeter", "fail", new Greeting("x", 1));
        Assert.Equal("not_found", failed.ErrorCode);
        Assert.Equal("No such greeting.", failed.Message);
    }

    [Fact]
    public async Task Services_UnavailableService_IsAFailedResult_AndRemovedWithItsOwner()
    {
        await StartAsync("test.provider", context =>
        {
            context.Services.Register("test.greeter", "Greeter", new Version(1, 0), new PluginOperations().Add<GreetingReply>("ping", (_, _) => ValueTask.FromResult(new GreetingReply("pong"))));
            return Task.CompletedTask;
        });
        var consumer = await StartAsync("test.consumer");
        Assert.True((await _engine.StopAsync(PluginId.Parse("test.provider"))).IsSuccess);

        var result = await consumer.Services.InvokeAsync<object?, GreetingReply>("test.greeter", "ping", null);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.ErrorCode, new[] { PluginErrorCodes.ServiceNotFound, PluginErrorCodes.ServiceUnavailable, PluginErrorCodes.ServiceRemoved });
        Assert.Null(consumer.Services.GetService("test.greeter"));
    }

    [Fact]
    public async Task Services_Cancellation_ReachesTheHandler()
    {
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartAsync("test.provider", context =>
        {
            context.Services.Register("test.slow", "Slow", new Version(1, 0), new PluginOperations().Add<GreetingReply>("wait", async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout, token);
                }
                catch (OperationCanceledException)
                {
                    observed.TrySetResult(true);
                    throw;
                }

                return new GreetingReply("late");
            }));
            return Task.CompletedTask;
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await _engine.Host.Services.InvokeAsync("test.slow", "wait", null, null, cancellation.Token);

        Assert.Equal(PluginOperationStatus.Canceled, result.Status);
        Assert.True(await observed.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task ExtensionPoints_HostDeclares_PluginContributes_HostInvokes()
    {
        await using var point = _engine.Host.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition("test.formatters", "Formatters", new Version(1, 0)) { Operations = ["format"] });
        await StartAsync("test.contributor", context =>
        {
            context.Extensions.Contribute(
                new ExtensionContributionDefinition("test.upper", "test.formatters", new Version(1, 0)) { DisplayName = "Upper case" },
                new PluginOperations().Add<Greeting, GreetingReply>("format", (greeting, _, _) => ValueTask.FromResult(new GreetingReply(greeting.Name.ToUpperInvariant()))));
            return Task.CompletedTask;
        });

        var contribution = Assert.Single(_engine.Host.Extensions.GetContributions("test.formatters"));
        Assert.Equal("Upper case", contribution.DisplayName);
        var result = await _engine.Host.Extensions.InvokeAsync<Greeting, GreetingReply>("test.formatters", "test.upper", "format", new Greeting("ada", 1));
        Assert.Equal("ADA", result.Value!.Text);

        Assert.True((await _engine.StopAsync(PluginId.Parse("test.contributor"))).IsSuccess);
        Assert.Empty(_engine.Host.Extensions.GetContributions("test.formatters"));
    }

    [Fact]
    public async Task BackgroundWork_StopsWithThePlugin()
    {
        var ticks = 0;
        var stopped = false;
        await StartAsync("test.worker", context =>
        {
            context.RunInBackground("ticker", async token =>
            {
                try
                {
                    while (true)
                    {
                        Interlocked.Increment(ref ticks);
                        await Task.Delay(10, token);
                    }
                }
                finally
                {
                    stopped = true;
                }
            });
            return Task.CompletedTask;
        });
        await Task.Delay(100);

        Assert.True((await _engine.StopAsync(PluginId.Parse("test.worker"))).IsSuccess);

        Assert.True(stopped, "Shutdown waits for the background work to finish.");
        Assert.True(Volatile.Read(ref ticks) > 0);
    }

    [Fact]
    public void Operations_RejectInvalidOrDuplicateNames_AndChangesAfterRegistration()
    {
        var operations = new PluginOperations().Add<GreetingReply>("ping", (_, _) => ValueTask.FromResult(new GreetingReply("pong")));

        Assert.Throws<ArgumentException>(() => operations.Add<GreetingReply>("ping", (_, _) => ValueTask.FromResult(new GreetingReply("x"))));
        Assert.Throws<ArgumentException>(() => operations.Add<GreetingReply>("Not Valid", (_, _) => ValueTask.FromResult(new GreetingReply("x"))));
        _ = operations.Handler;
        Assert.Throws<InvalidOperationException>(() => operations.Add<GreetingReply>("late", (_, _) => ValueTask.FromResult(new GreetingReply("x"))));
    }

    private sealed class DelegatePlugin(Func<IPluginContext, Task> initialize) : IPlugin
    {
        public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => initialize(context);

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DelegateFactory(IPlugin plugin) : IPluginInstanceFactory
    {
        public ValueTask<IPlugin> CreateAsync(PluginDescriptor descriptor, CancellationToken cancellationToken) => ValueTask.FromResult(plugin);

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
    }
}
