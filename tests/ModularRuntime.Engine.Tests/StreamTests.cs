using ModularRuntime.Contracts;
using ModularRuntime.TestSupport;
using static ModularRuntime.Engine.Tests.PlatformTestHelpers;

namespace ModularRuntime.Engine.Tests;

public sealed class StreamTests
{
    private const string StreamId = "test.ticks";

    private static StreamDefinition Definition(int? capacity = null, StreamFullMode mode = StreamFullMode.Wait) =>
        new(StreamId, "Ticks", new Version(1, 0)) { DefaultCapacity = capacity, DefaultFullMode = mode };

    [Fact]
    public async Task Create_Subscribe_Read_WithSequenceNumbers_AndCompletion()
    {
        await using var engine = new PluginEngine();
        IStreamProducer<int>? producer = null;
        await StartPluginAsync(engine, "com.test.producer", c =>
        {
            producer = c.Streams.Create<int>(Definition() with { Metadata = new Dictionary<string, string> { ["unit"] = "ms" } });
            return Task.CompletedTask;
        });

        var descriptor = Assert.Single(engine.Host.Streams.GetStreams());
        Assert.Equal(StreamId, descriptor.StreamId);
        Assert.Equal(typeof(int).FullName, descriptor.ItemTypeName);
        Assert.Equal("ms", descriptor.Metadata["unit"]);
        Assert.Equal(0, descriptor.SubscriberCount);
        Assert.Equal(ResourceKind.Stream, producer!.Kind);

        var subscription = engine.Host.Streams.Subscribe<int>(StreamId)!;
        Assert.Equal(ResourceKind.StreamSubscription, subscription.Kind);
        Assert.Equal(descriptor.RegistrationId, subscription.StreamRegistrationId);
        Assert.Equal(1, producer.SubscriberCount);

        Assert.True(await producer.WriteAsync(10));
        Assert.True(await producer.WriteAsync(20));
        Assert.True(producer.TryWrite(30));
        producer.Complete();
        Assert.False(await producer.WriteAsync(40));
        Assert.False(producer.TryWrite(50));

        var received = new List<StreamMessage<int>>();
        await foreach (var message in subscription.ReadAllAsync())
        {
            received.Add(message);
        }

        Assert.Equal([(1, 10), (2, 20), (3, 30)], received.Select(m => (m.Sequence, m.Item)));
        Assert.Equal(PluginErrorCodes.StreamCompleted, subscription.Completion!.ErrorCode);
        Assert.Equal(0, subscription.DroppedCount);
        Assert.Equal(3, producer.Sequence);
        Assert.False(engine.Host.Streams.GetStream(StreamId)!.IsAvailable);
    }

    [Fact]
    public async Task ItemsOfPrivateTypes_AreWarnedOnce_OrRejected_WhenTheItemTypeIsNotSealed()
    {
        var logger = new CollectingLogger();
        await using var warning = new PluginEngine(logger, new RuntimeLimits { TypedValueCheck = TypedValueCheck.Warn }, new ExcludingPolicy(typeof(PrivateValue)));
        var producer = warning.Host.Streams.Create<object>(Definition(capacity: 8));
        var subscription = warning.Host.Streams.Subscribe<object>(StreamId)!;

        Assert.True(await producer.WriteAsync(new PrivateValue { Number = 1 }));
        Assert.True(producer.TryWrite(new PrivateValue { Number = 2 }));
        Assert.True(await producer.WriteAsync("shared"));
        Assert.True(await producer.WriteAsync(new List<PrivateValue>()));
        Assert.Equal(2, logger.Entries.Count(e => e.Message.Contains(PluginErrorCodes.ValueNotShared, StringComparison.Ordinal)));
        Assert.Equal(4, producer.Sequence);
        Assert.True(subscription.Reader.TryRead(out var first));
        Assert.IsType<PrivateValue>(first.Item);

        await using var rejecting = new PluginEngine(sharedTypePolicy: new ExcludingPolicy(typeof(PrivateValue)));
        var strict = rejecting.Host.Streams.Create<object>(Definition(capacity: 8));
        Assert.Equal(PluginErrorCodes.ValueNotShared, (await Assert.ThrowsAsync<PluginRuntimeException>(async () => await strict.WriteAsync(new PrivateValue()))).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<PluginRuntimeException>(() => strict.TryWrite(new PrivateValue())).ErrorCode);
        Assert.True(await strict.WriteAsync("shared"));
        Assert.Equal(1, strict.Sequence);

        // A sealed shared item type needs no per-item check (and a private item type is refused at creation).
        await using var unchecked_ = new PluginEngine(sharedTypePolicy: new ExcludingPolicy(typeof(PrivateValue)));
        Assert.True(await unchecked_.Host.Streams.Create<string>(Definition()).WriteAsync("fine"));
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<PluginRuntimeException>(() => unchecked_.Host.Streams.Create<PrivateValue>(new StreamDefinition("test.private", "Private", new Version(1, 0)))).ErrorCode);
    }

    [Fact]
    public async Task WaitMode_AppliesBackpressure_ToTheProducer()
    {
        await using var engine = new PluginEngine();
        var producer = engine.Host.Streams.Create<int>(Definition(capacity: 2));
        var subscription = engine.Host.Streams.Subscribe<int>(StreamId)!;

        Assert.True(await producer.WriteAsync(1));
        Assert.True(await producer.WriteAsync(2));
        Assert.False(producer.TryWrite(3), "a full Wait subscriber refuses TryWrite");
        Assert.Equal(1, subscription.DroppedCount);

        var blocked = producer.WriteAsync(4).AsTask();
        await Task.Delay(100);
        Assert.False(blocked.IsCompleted, "WriteAsync waits while the subscriber's buffer is full");

        Assert.True(subscription.Reader.TryRead(out var first));
        Assert.Equal((1, 1), (first.Sequence, first.Item));
        Assert.True(await blocked.WaitAsync(TestTimeout));

        using var cancellation = new CancellationTokenSource();
        var canceled = producer.WriteAsync(5, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task DropModes_NeverBlock_AndReportLoss()
    {
        await using var engine = new PluginEngine();
        var producer = engine.Host.Streams.Create<int>(Definition(capacity: 2, mode: StreamFullMode.DropOldest));
        var oldest = engine.Host.Streams.Subscribe<int>(StreamId)!;
        var newest = engine.Host.Streams.Subscribe<int>(StreamId, new StreamSubscribeOptions { FullMode = StreamFullMode.DropNewest })!;
        var large = engine.Host.Streams.Subscribe<int>(StreamId, new StreamSubscribeOptions { Capacity = 10 })!;

        for (var value = 1; value <= 5; value++)
        {
            Assert.True(await producer.WriteAsync(value).AsTask().WaitAsync(TestTimeout));
        }

        producer.Complete();
        Assert.Equal([4, 5], await ReadItemsAsync(oldest));
        Assert.Equal(3, oldest.DroppedCount);
        Assert.Equal([1, 2], await ReadItemsAsync(newest));
        Assert.Equal(3, newest.DroppedCount);
        Assert.Equal([1, 2, 3, 4, 5], await ReadItemsAsync(large));
        Assert.Equal(0, large.DroppedCount);

        var gaps = new List<long>();
        await foreach (var message in oldest.ReadAllAsync())
        {
            gaps.Add(message.Sequence);
        }

        Assert.Empty(gaps);
    }

    [Fact]
    public async Task ProducerStop_CompletesSubscribers_WithStreamRemoved_AndReleasesTheirSubscriptions()
    {
        await using var engine = new PluginEngine();
        IStreamProducer<string>? producer = null;
        await StartPluginAsync(engine, "com.test.producer", c =>
        {
            producer = c.Streams.Create<string>(Definition());
            return Task.CompletedTask;
        });
        IStreamSubscription<string>? subscription = null;
        await StartPluginAsync(engine, "com.test.consumer", c =>
        {
            subscription = c.Streams.Subscribe<string>(StreamId);
            return Task.CompletedTask;
        });
        Assert.True(await producer!.WriteAsync("before"));
        Assert.Single(engine.GetResources(PluginId.Parse("com.test.consumer")));

        Assert.True((await engine.StopAsync(PluginId.Parse("com.test.producer"))).IsSuccess);

        Assert.Empty(engine.Host.Streams.GetStreams());
        Assert.Equal(["before"], await ReadItemsAsync(subscription!));
        Assert.Equal(PluginErrorCodes.StreamRemoved, subscription!.Completion!.ErrorCode);
        Assert.False(subscription.IsActive);
        Assert.Empty(engine.GetResources(PluginId.Parse("com.test.consumer")));
        Assert.False(await producer.WriteAsync("after"));
        Assert.Null(engine.Host.Streams.Subscribe<string>(StreamId));
    }

    [Fact]
    public async Task SubscriberStop_DetachesItsSubscription_WithoutAffectingOthers()
    {
        await using var engine = new PluginEngine();
        var producer = engine.Host.Streams.Create<int>(Definition(capacity: 1));
        var host = engine.Host.Streams.Subscribe<int>(StreamId)!;
        IStreamSubscription<int>? pluginSubscription = null;
        await StartPluginAsync(engine, "com.test.consumer", c =>
        {
            pluginSubscription = c.Streams.Subscribe<int>(StreamId);
            return Task.CompletedTask;
        });
        Assert.Equal(2, producer.SubscriberCount);
        Assert.True(await producer.WriteAsync(1));

        // The plugin never reads: with Wait mode it would block the producer once it stopped reading,
        // but stopping the plugin detaches it.
        Assert.True((await engine.StopAsync(PluginId.Parse("com.test.consumer"))).IsSuccess);
        Assert.Equal(1, producer.SubscriberCount);
        Assert.Equal(PluginErrorCodes.SubscriptionDisposed, pluginSubscription!.Completion!.ErrorCode);

        Assert.True(host.Reader.TryRead(out _));
        Assert.True(await producer.WriteAsync(2).AsTask().WaitAsync(TestTimeout));
        await host.DisposeAsync();
        Assert.Equal(0, producer.SubscriberCount);
        Assert.True(await producer.WriteAsync(3), "writing with no subscribers succeeds and discards");
    }

    [Fact]
    public async Task Validation_Duplicates_TypeMismatch_AndLimits()
    {
        await using var engine = new PluginEngine(limits: new RuntimeLimits { MaxStreamCapacity = 16, DefaultStreamCapacity = 4 });
        var streams = engine.Host.Streams;
        var producer = streams.Create<int>(Definition());

        Assert.Equal(4, producer.Descriptor.DefaultCapacity);
        Assert.Equal(PluginErrorCodes.DuplicateStream, Assert.Throws<PluginRuntimeException>(() => streams.Create<int>(Definition())).ErrorCode);
        Assert.Equal(PluginErrorCodes.StreamItemTypeMismatch, Assert.Throws<PluginRuntimeException>(() => streams.Subscribe<string>(StreamId)).ErrorCode);
        Assert.Throws<ArgumentOutOfRangeException>(() => streams.Subscribe<int>(StreamId, new StreamSubscribeOptions { Capacity = 17 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => streams.Subscribe<int>(StreamId, new StreamSubscribeOptions { Capacity = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => streams.Create<int>(new StreamDefinition("test.other", "x", new Version(1, 0)) { DefaultCapacity = 100 }));
        Assert.Throws<ArgumentException>(() => streams.Create<int>(new StreamDefinition("Bad Id", "x", new Version(1, 0))));
        Assert.Null(streams.Subscribe<int>(StreamId, new StreamSubscribeOptions { MinimumVersion = new Version(2, 0) }));
        Assert.Null(streams.Subscribe<int>("test.missing"));
        Assert.Null(streams.GetStream("test.missing"));
    }

    [Fact]
    public async Task BinaryItems_AreCopiedOnWrite_UnlessSharingIsRequested()
    {
        await using var engine = new PluginEngine();
        var copying = engine.Host.Streams.Create<ReadOnlyMemory<byte>>(Definition());
        var sharing = engine.Host.Streams.Create<ReadOnlyMemory<byte>>(new StreamDefinition("test.shared", "Shared", new Version(1, 0)) { CopyBinaryItems = false });
        var copied = engine.Host.Streams.Subscribe<ReadOnlyMemory<byte>>(StreamId)!;
        var shared = engine.Host.Streams.Subscribe<ReadOnlyMemory<byte>>("test.shared")!;

        var buffer = new byte[] { 1, 2, 3 };
        Assert.True(await copying.WriteAsync(buffer));
        Assert.True(await sharing.WriteAsync(buffer));
        buffer[0] = 99;

        Assert.True(copied.Reader.TryRead(out var copy));
        Assert.Equal([1, 2, 3], copy.Item.ToArray());
        Assert.True(shared.Reader.TryRead(out var same));
        Assert.Equal([99, 2, 3], same.Item.ToArray());
    }

    [Fact]
    public async Task ConcurrentProducers_DeliverEveryItemToEveryConsumer()
    {
        await using var engine = new PluginEngine();
        var producer = engine.Host.Streams.Create<int>(Definition(capacity: 64));
        var consumers = Enumerable.Range(0, 4).Select(_ => engine.Host.Streams.Subscribe<int>(StreamId)!).ToArray();
        var readers = consumers.Select(async c =>
        {
            var items = new List<int>();
            await foreach (var message in c.ReadAllAsync())
            {
                items.Add(message.Item);
            }

            return items;
        }).ToArray();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(async () =>
        {
            for (var i = 0; i < 250; i++)
            {
                Assert.True(await producer.WriteAsync(writer * 1000 + i));
            }
        }))).WaitAsync(TestTimeout);
        producer.Complete();

        var results = await Task.WhenAll(readers).WaitAsync(TestTimeout);
        foreach (var items in results)
        {
            Assert.Equal(2000, items.Count);
            Assert.Equal(2000, items.Distinct().Count());
        }

        Assert.Equal(2000, producer.Sequence);
    }

    [Fact]
    public async Task TwoRuntimes_DoNotShareStreams()
    {
        await using var first = new PluginEngine();
        await using var second = new PluginEngine();
        first.Host.Streams.Create<int>(Definition());

        Assert.Empty(second.Host.Streams.GetStreams());
        Assert.Null(second.Host.Streams.Subscribe<int>(StreamId));
    }

    private static async Task<List<T>> ReadItemsAsync<T>(IStreamSubscription<T> subscription)
    {
        var items = new List<T>();
        await foreach (var message in subscription.ReadAllAsync())
        {
            items.Add(message.Item);
        }

        return items;
    }
}
