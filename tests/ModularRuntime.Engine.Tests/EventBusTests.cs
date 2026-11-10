using ModularRuntime.Contracts;
using ModularRuntime.TestSupport;
using static ModularRuntime.Engine.Tests.PlatformTestHelpers;

namespace ModularRuntime.Engine.Tests;

public sealed class EventBusTests
{
    private const string Topic = "test.topic";

    [Fact]
    public async Task Publish_WithoutSubscribers_Succeeds()
    {
        await using var engine = new PluginEngine();

        var result = await engine.Host.Events.PublishAsync(Topic, Json("{}"));

        Assert.Equal(PluginOperationStatus.Succeeded, result.Status);
        Assert.Empty(result.Deliveries);
    }

    [Fact]
    public async Task SingleSubscriber_ReceivesEventWithSourceCorrelationAndMetadata()
    {
        await using var engine = new PluginEngine();
        PluginEvent? received = null;
        await StartPluginAsync(engine, "com.test.subscriber", context =>
        {
            context.Events.Subscribe(Topic, (e, _) =>
            {
                received = e;
                return ValueTask.CompletedTask;
            });
            return Task.CompletedTask;
        });

        var result = await engine.Host.Events.PublishAsync(
            Topic,
            Json("""{"value":42}"""),
            new EventPublishOptions { CorrelationId = "corr-1", Metadata = new Dictionary<string, string> { ["k"] = "v" } });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.SucceededCount);
        Assert.NotNull(received);
        Assert.Equal(Topic, received.Topic);
        Assert.Equal("""{"value":42}""", received.Payload.Json);
        Assert.True(received.Source.IsHost);
        Assert.Equal(engine.RuntimeId, received.Source.RuntimeId);
        Assert.Equal("corr-1", received.CorrelationId);
        Assert.Equal("v", received.Metadata["k"]);
        Assert.Equal(result.EventId, received.EventId);
    }

    [Fact]
    public async Task MultipleSubscribers_ReceiveInSubscriptionOrder()
    {
        await using var engine = new PluginEngine();
        var order = new List<string>();
        engine.Host.Events.Subscribe(Topic, (_, _) => { order.Add("host"); return ValueTask.CompletedTask; });
        await StartPluginAsync(engine, "com.test.first", c => { c.Events.Subscribe(Topic, (_, _) => { order.Add("first"); return ValueTask.CompletedTask; }); return Task.CompletedTask; });
        await StartPluginAsync(engine, "com.test.second", c => { c.Events.Subscribe(Topic, async (_, _) => { await Task.Yield(); order.Add("second"); }); return Task.CompletedTask; });

        var result = await engine.Host.Events.PublishAsync(Topic, Json("1"));

        Assert.Equal(["host", "first", "second"], order);
        Assert.Equal(3, result.SucceededCount);
        Assert.Equal(3, engine.Host.Events.GetSubscriptions(Topic).Count);
    }

    [Fact]
    public async Task Unsubscribe_StopsDeliveryAndDisposeIsIdempotent()
    {
        await using var engine = new PluginEngine();
        var count = 0;
        var subscription = engine.Host.Events.Subscribe(Topic, (_, _) => { count++; return ValueTask.CompletedTask; });

        await engine.Host.Events.PublishAsync(Topic, Json("1"));
        Assert.True(subscription.IsActive);
        await subscription.DisposeAsync();
        await subscription.DisposeAsync();
        await engine.Host.Events.PublishAsync(Topic, Json("2"));

        Assert.Equal(1, count);
        Assert.False(subscription.IsActive);
        Assert.Empty(engine.Host.Events.GetSubscriptions());
        Assert.Empty(engine.Host.Resources.GetResources());
    }

    [Fact]
    public async Task FailingHandler_IsIsolatedAndReported()
    {
        var logger = new CollectingLogger();
        await using var engine = new PluginEngine(logger);
        var secondCalled = false;
        engine.Host.Events.Subscribe(Topic, (_, _) => throw new InvalidOperationException("handler boom"));
        engine.Host.Events.Subscribe(Topic, (_, _) => { secondCalled = true; return ValueTask.CompletedTask; });

        var result = await engine.Host.Events.PublishAsync(Topic, Json("{}"));

        Assert.Equal(PluginOperationStatus.Failed, result.Status);
        Assert.True(secondCalled);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.SucceededCount);
        var failed = result.Deliveries.Single(d => d.Status == PluginOperationStatus.Failed);
        Assert.Equal(PluginErrorCodes.HandlerFailed, failed.ErrorCode);
        Assert.Equal("System.InvalidOperationException", failed.Diagnostic!.ExceptionType);
        Assert.NotNull(logger.Find(LogLevel.Warning, "Event handler for topic"));
    }

    [Fact]
    public async Task HandlerPublishingAnotherEvent_IsDeliveredInline()
    {
        await using var engine = new PluginEngine();
        var received = new List<string>();
        engine.Host.Events.Subscribe("test.second", (e, _) => { received.Add(e.Topic); return ValueTask.CompletedTask; });
        await StartPluginAsync(engine, "com.test.relay", c =>
        {
            c.Events.Subscribe(Topic, async (e, ct) =>
            {
                received.Add(e.Topic);
                var nested = await c.Events.PublishAsync("test.second", e.Payload, cancellationToken: ct);
                Assert.True(nested.IsSuccess);
            });
            return Task.CompletedTask;
        });

        var result = await engine.Host.Events.PublishAsync(Topic, Json("{}")).AsTask().WaitAsync(TestTimeout);

        Assert.True(result.IsSuccess);
        Assert.Equal([Topic, "test.second"], received);
    }

    [Fact]
    public async Task RecursivePublishing_IsBoundedByDispatchDepth()
    {
        await using var engine = new PluginEngine(limits: new RuntimeLimits { MaxEventDispatchDepth = 4 });
        var depth = 0;
        EventPublishResult? deepest = null;
        engine.Host.Events.Subscribe(Topic, async (e, ct) =>
        {
            depth++;
            var nested = await engine.Host.Events.PublishAsync(Topic, e.Payload, cancellationToken: ct);
            if (nested.Status == PluginOperationStatus.Rejected)
            {
                deepest = nested;
            }
        });

        var result = await engine.Host.Events.PublishAsync(Topic, Json("{}")).AsTask().WaitAsync(TestTimeout);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, depth);
        Assert.Equal(PluginErrorCodes.EventDispatchDepthExceeded, deepest!.ErrorCode);
    }

    [Fact]
    public async Task CanceledPublication_ReportsCanceledDeliveries()
    {
        await using var engine = new PluginEngine();
        using var cancellation = new CancellationTokenSource();
        var secondCalled = false;
        engine.Host.Events.Subscribe(Topic, async (_, ct) =>
        {
            await cancellation.CancelAsync();
            ct.ThrowIfCancellationRequested();
        });
        engine.Host.Events.Subscribe(Topic, (_, _) => { secondCalled = true; return ValueTask.CompletedTask; });

        var result = await engine.Host.Events.PublishAsync(Topic, Json("{}"), cancellationToken: cancellation.Token);

        Assert.Equal(PluginOperationStatus.Canceled, result.Status);
        Assert.False(secondCalled);
        Assert.Equal(2, result.CanceledCount);

        var before = await engine.Host.Events.PublishAsync(Topic, Json("{}"), cancellationToken: cancellation.Token);
        Assert.Equal(PluginOperationStatus.Canceled, before.Status);
        Assert.Empty(before.Deliveries);
    }

    [Theory]
    [InlineData("Invalid Topic")]
    [InlineData("")]
    [InlineData(".leading")]
    public async Task InvalidTopic_IsRejected(string topic)
    {
        await using var engine = new PluginEngine();

        var result = await engine.Host.Events.PublishAsync(topic, Json("{}"));

        Assert.Equal(PluginOperationStatus.Rejected, result.Status);
        Assert.Equal(PluginErrorCodes.InvalidIdentifier, result.ErrorCode);
        Assert.Throws<ArgumentException>(() => engine.Host.Events.Subscribe(topic, (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public void InvalidJson_CannotBecomeAPayload()
    {
        Assert.False(JsonPayload.TryParse("{ not json", out _));
        Assert.False(JsonPayload.TryParse("{\"a\":1,}", out _));
        Assert.False(JsonPayload.TryParse("// comment\n{}", out _));
        Assert.Throws<FormatException>(() => JsonPayload.Parse(new string('[', 100) + new string(']', 100)));
        Assert.True(JsonPayload.TryParse("""{"text":"ok"}""", out var payload));
        Assert.Equal("ok", payload.GetString("text"));
    }

    [Fact]
    public async Task OversizedPayload_IsRejectedWithoutDelivery()
    {
        await using var engine = new PluginEngine(limits: new RuntimeLimits { MaxPayloadBytes = 64 });
        var called = false;
        engine.Host.Events.Subscribe(Topic, (_, _) => { called = true; return ValueTask.CompletedTask; });

        var result = await engine.Host.Events.PublishAsync(Topic, JsonPayload.FromString(new string('x', 100)));

        Assert.Equal(PluginOperationStatus.Rejected, result.Status);
        Assert.Equal(PluginErrorCodes.PayloadTooLarge, result.ErrorCode);
        Assert.False(called);
    }

    [Fact]
    public async Task InvalidMetadata_IsRejected()
    {
        await using var engine = new PluginEngine();
        var metadata = Enumerable.Range(0, 40).ToDictionary(i => $"k{i}", i => "v");

        var result = await engine.Host.Events.PublishAsync(Topic, Json("{}"), new EventPublishOptions { Metadata = metadata });

        Assert.Equal(PluginErrorCodes.InvalidMetadata, result.ErrorCode);
    }

    [Fact]
    public async Task SubscriptionCreatedDuringPublish_ReceivesOnlyLaterEvents()
    {
        await using var engine = new PluginEngine();
        var lateCount = 0;
        IEventSubscription? late = null;
        engine.Host.Events.Subscribe(Topic, (_, _) =>
        {
            late ??= engine.Host.Events.Subscribe(Topic, (_, _) => { lateCount++; return ValueTask.CompletedTask; });
            return ValueTask.CompletedTask;
        });

        var first = await engine.Host.Events.PublishAsync(Topic, Json("1"));
        Assert.Equal(0, lateCount);
        Assert.Single(first.Deliveries);

        var second = await engine.Host.Events.PublishAsync(Topic, Json("2"));
        Assert.Equal(1, lateCount);
        Assert.Equal(2, second.Deliveries.Count);
    }

    [Fact]
    public async Task UnsubscribeDuringDispatch_PreventsLaterInvocation()
    {
        await using var engine = new PluginEngine();
        var secondCalled = false;
        IEventSubscription? second = null;
        engine.Host.Events.Subscribe(Topic, async (_, _) => await second!.DisposeAsync());
        second = engine.Host.Events.Subscribe(Topic, (_, _) => { secondCalled = true; return ValueTask.CompletedTask; });

        var result = await engine.Host.Events.PublishAsync(Topic, Json("{}"));

        Assert.False(secondCalled);
        Assert.Single(result.Deliveries);
    }

    [Fact]
    public async Task SubscriptionDisposingItselfInsideItsHandler_DoesNotDeadlock()
    {
        await using var engine = new PluginEngine();
        var count = 0;
        IEventSubscription? self = null;
        self = engine.Host.Events.Subscribe(Topic, async (_, _) =>
        {
            count++;
            await self!.DisposeAsync();
        });

        await engine.Host.Events.PublishAsync(Topic, Json("1")).AsTask().WaitAsync(TestTimeout);
        await engine.Host.Events.PublishAsync(Topic, Json("2")).AsTask().WaitAsync(TestTimeout);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ConcurrentPublishes_AreAllDelivered()
    {
        await using var engine = new PluginEngine();
        var received = 0;
        await StartPluginAsync(engine, "com.test.counter", c =>
        {
            c.Events.Subscribe(Topic, async (_, _) => { await Task.Yield(); Interlocked.Increment(ref received); });
            return Task.CompletedTask;
        });

        var publishers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                Assert.True((await engine.Host.Events.PublishAsync(Topic, Json("{}"))).IsSuccess);
            }
        }));
        await Task.WhenAll(publishers).WaitAsync(TestTimeout);

        Assert.Equal(400, Volatile.Read(ref received));
    }

    [Fact]
    public async Task ConcurrentSubscribeUnsubscribeAndPublish_LeaveConsistentState()
    {
        await using var engine = new PluginEngine();
        using var stop = new CancellationTokenSource();
        var publisher = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var result = await engine.Host.Events.PublishAsync(Topic, Json("{}"));
                Assert.NotEqual(PluginOperationStatus.Failed, result.Status);
            }
        });

        var churn = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var subscription = engine.Host.Events.Subscribe(Topic, (_, _) => ValueTask.CompletedTask);
                await subscription.DisposeAsync();
            }
        }));
        await Task.WhenAll(churn).WaitAsync(TestTimeout);
        await stop.CancelAsync();
        await publisher.WaitAsync(TestTimeout);

        Assert.Empty(engine.Host.Events.GetSubscriptions());
        Assert.Empty(engine.Host.Resources.GetResources());
    }

    [Fact]
    public async Task SubscriberStoppingDuringDelivery_IsCanceledDrainedAndRemoved()
    {
        await using var engine = new PluginEngine();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerFinished = false;
        var shutdownSawHandlerFinished = false;
        await StartPluginAsync(
            engine,
            "com.test.slow-subscriber",
            c =>
            {
                c.Events.Subscribe(Topic, async (_, ct) =>
                {
                    entered.TrySetResult();
                    try
                    {
                        await Task.Delay(System.Threading.Timeout.Infinite, ct);
                    }
                    finally
                    {
                        // Asynchronous cleanup after cancellation: only a real drain waits for it.
                        await Task.Delay(50, CancellationToken.None);
                        handlerFinished = true;
                    }
                });
                return Task.CompletedTask;
            },
            onShutdown: _ =>
            {
                shutdownSawHandlerFinished = handlerFinished;
                return Task.CompletedTask;
            });

        var publish = engine.Host.Events.PublishAsync(Topic, Json("{}")).AsTask();
        await entered.Task.WaitAsync(TestTimeout);

        var stop = await engine.StopAsync(PluginId.Parse("com.test.slow-subscriber")).WaitAsync(TestTimeout);
        var result = await publish.WaitAsync(TestTimeout);

        Assert.True(stop.IsSuccess);
        Assert.True(shutdownSawHandlerFinished, "ShutdownAsync must run after in-flight handlers drained.");
        Assert.Equal(PluginOperationStatus.Canceled, Assert.Single(result.Deliveries).Status);
        Assert.Empty(engine.Host.Events.GetSubscriptions());
        Assert.Empty((await engine.Host.Events.PublishAsync(Topic, Json("{}"))).Deliveries);
    }

    [Fact]
    public async Task StoppingPlugin_CanPublishButCannotSubscribeDuringShutdown()
    {
        await using var engine = new PluginEngine();
        var hostReceived = 0;
        engine.Host.Events.Subscribe("test.goodbye", (_, _) => { hostReceived++; return ValueTask.CompletedTask; });
        IPluginContext? context = null;
        EventPublishResult? goodbye = null;
        PluginRuntimeException? subscribeError = null;
        await StartPluginAsync(
            engine,
            "com.test.goodbye",
            c => { context = c; return Task.CompletedTask; },
            onShutdown: async _ =>
            {
                goodbye = await context!.Events.PublishAsync("test.goodbye", Json("{}"), cancellationToken: CancellationToken.None);
                subscribeError = Assert.Throws<PluginRuntimeException>(() =>
                    context.Events.Subscribe(Topic, (_, _) => ValueTask.CompletedTask));
            });

        Assert.True((await engine.StopAsync(PluginId.Parse("com.test.goodbye"))).IsSuccess);

        Assert.True(goodbye!.IsSuccess);
        Assert.Equal(1, hostReceived);
        Assert.Equal(PluginErrorCodes.ScopeClosed, subscribeError!.ErrorCode);

        var afterStop = await context!.Events.PublishAsync(Topic, Json("{}"));
        Assert.Equal(PluginErrorCodes.ScopeClosed, afterStop.ErrorCode);
    }

    [Fact]
    public async Task Subscriptions_AreRemovedAutomaticallyWhenThePluginStops()
    {
        await using var engine = new PluginEngine();
        await StartPluginAsync(engine, "com.test.auto", c =>
        {
            c.Events.Subscribe(Topic, (_, _) => ValueTask.CompletedTask);
            c.Events.Subscribe("test.other", (_, _) => ValueTask.CompletedTask);
            return Task.CompletedTask;
        });
        var subscriptions = engine.Host.Events.GetSubscriptions();
        Assert.Equal(2, subscriptions.Count);
        Assert.All(subscriptions, s => Assert.Equal(PluginId.Parse("com.test.auto"), s.Subscriber.PluginId));

        await engine.StopAsync(PluginId.Parse("com.test.auto"));

        Assert.Empty(engine.Host.Events.GetSubscriptions());
    }

    [Fact]
    public async Task FailedInitialization_RemovesSubscriptionsCreatedBeforeTheFailure()
    {
        await using var engine = new PluginEngine();
        var (_, result) = await TryStartPluginAsync(engine, "com.test.init-fails", c =>
        {
            c.Events.Subscribe(Topic, (_, _) => ValueTask.CompletedTask);
            throw new InvalidOperationException("init failure after subscribing");
        });

        Assert.Equal(PluginErrorCodes.InitializationFailed, result.ErrorCode);
        Assert.Empty(engine.Host.Events.GetSubscriptions());
        Assert.Empty((await engine.Host.Events.PublishAsync(Topic, Json("{}"))).Deliveries);
    }

    [Fact]
    public async Task TwoRuntimes_DoNotShareEvents()
    {
        await using var first = new PluginEngine();
        await using var second = new PluginEngine();
        var secondReceived = 0;
        second.Host.Events.Subscribe(Topic, (_, _) => { secondReceived++; return ValueTask.CompletedTask; });

        var result = await first.Host.Events.PublishAsync(Topic, Json("{}"));

        Assert.Empty(result.Deliveries);
        Assert.Equal(0, secondReceived);
        Assert.Empty(first.Host.Events.GetSubscriptions());
        Assert.NotEqual(first.RuntimeId, second.RuntimeId);
    }

    [Fact]
    public async Task DisposedRuntime_RejectsPublishAndSubscribe()
    {
        var engine = new PluginEngine();
        var host = engine.Host;
        host.Events.Subscribe(Topic, (_, _) => ValueTask.CompletedTask);

        await engine.DisposeAsync();

        Assert.Empty(host.Events.GetSubscriptions());
        Assert.Equal(PluginErrorCodes.EngineDisposed, (await host.Events.PublishAsync(Topic, Json("{}"))).ErrorCode);
        AssertRuntimeError(PluginErrorCodes.EngineDisposed, () => host.Events.Subscribe(Topic, (_, _) => ValueTask.CompletedTask));
    }
}
