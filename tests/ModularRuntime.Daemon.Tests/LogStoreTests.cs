using ModularRuntime.Contracts;
using ModularRuntime.Ipc.Contracts;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Daemon.Tests;

public sealed class LogStoreTests
{
    private static LogStore Create(int capacity, params string[] messages)
    {
        var store = new LogStore(capacity, logDirectory: null);
        foreach (var message in messages)
        {
            store.Write(LogLevel.Information, "Test", message);
        }

        return store;
    }

    [Fact]
    public async Task RingBuffer_KeepsTheNewestEntries_WithIncreasingSequences()
    {
        await using var store = Create(3, "a", "b", "c", "d", "e");

        var result = store.Query(null, afterSequence: null, limit: 100);

        Assert.Equal(["c", "d", "e"], result.Entries.Select(e => e.Message));
        Assert.Equal([3L, 4L, 5L], result.Entries.Select(e => e.Sequence));
        Assert.Equal(5, result.LastSequence);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Query_WithoutAfter_ReturnsTheNewestLimitEntriesInOrder()
    {
        await using var store = Create(100, "1", "2", "3", "4");

        var result = store.Query(null, null, limit: 2);

        Assert.Equal(["3", "4"], result.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Query_AfterSequence_ReturnsOldestFirst_AndReportsTruncationWhenEntriesWereEvicted()
    {
        await using var store = Create(3, "a", "b", "c", "d", "e");

        var contiguous = store.Query(null, afterSequence: 3, limit: 1);
        var gap = store.Query(null, afterSequence: 1, limit: 100);

        Assert.Equal(["d"], contiguous.Entries.Select(e => e.Message));
        Assert.False(contiguous.Truncated);
        Assert.True(gap.Truncated, "Entry 2 was evicted, so the caller missed entries.");
        Assert.Equal(["c", "d", "e"], gap.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Filters_ByLevelPluginCategoryAndText()
    {
        await using var store = new LogStore(100, null);
        store.Log(new LogEntry(LogLevel.Debug, "noise", "Engine"));
        store.Log(new LogEntry(LogLevel.Warning, "Disk almost full", "Plugin", PluginId: new PluginId("com.example.disk")));
        store.Log(new LogEntry(LogLevel.Error, "failed", "Plugin", PluginId: new PluginId("com.example.other"),
            Diagnostic: new PluginDiagnostic("System.IO.IOException", "disk unavailable", null)));

        Assert.Equal(2, store.Query(new LogFilterDto(MinimumLevel: LogLevelDto.Warning), null, 100).Entries.Count);
        Assert.Single(store.Query(new LogFilterDto(PluginId: "com.example.disk"), null, 100).Entries);
        Assert.Equal(2, store.Query(new LogFilterDto(Category: "plugin"), null, 100).Entries.Count);
        Assert.Equal(2, store.Query(new LogFilterDto(Text: "DISK"), null, 100).Entries.Count); // message or diagnostic
        Assert.Empty(store.Query(new LogFilterDto(Until: DateTimeOffset.UtcNow.AddDays(-1)), null, 100).Entries);
    }

    [Fact]
    public async Task Subscription_ReceivesOnlyNewMatchingEntries_InOrder()
    {
        await using var store = Create(100, "before");
        using var subscription = store.Subscribe(new LogFilterDto(Text: "keep"));

        store.Write(LogLevel.Information, "Test", "keep 1");
        store.Write(LogLevel.Information, "Test", "skip");
        store.Write(LogLevel.Information, "Test", "keep 2");

        Assert.Equal(1, subscription.StartSequence);
        Assert.True(subscription.Reader.TryRead(out var first));
        Assert.True(subscription.Reader.TryRead(out var second));
        Assert.False(subscription.Reader.TryRead(out _));
        Assert.Equal(["keep 1", "keep 2"], new[] { first!.Message, second!.Message });
        Assert.True(first.Sequence < second.Sequence);
    }

    [Fact]
    public async Task SlowSubscriber_DropsInsteadOfBlocking_AndCountsTheDrops()
    {
        await using var store = new LogStore(100, null);
        using var subscription = store.Subscribe(null);
        var total = LogStore.SubscriptionQueueCapacity + 25;

        for (var i = 0; i < total; i++)
        {
            store.Write(LogLevel.Information, "Test", $"m{i}");
        }

        var received = 0;
        while (subscription.Reader.TryRead(out _))
        {
            received++;
        }

        Assert.Equal(LogStore.SubscriptionQueueCapacity, received);
        Assert.Equal(25, subscription.TakeDropped());
        Assert.Equal(0, subscription.TakeDropped());
    }

    [Fact]
    public async Task DisposedSubscription_StopsReceiving()
    {
        await using var store = new LogStore(100, null);
        var subscription = store.Subscribe(null);
        subscription.Dispose();

        store.Write(LogLevel.Information, "Test", "after");

        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ConcurrentWriters_ProduceUniqueContiguousSequences()
    {
        await using var store = new LogStore(10_000, null);
        using var subscription = store.Subscribe(null);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                store.Write(LogLevel.Information, "Test", $"{w}:{i}");
            }
        })));

        var sequences = new List<long>();
        while (subscription.Reader.TryRead(out var entry))
        {
            sequences.Add(entry.Sequence);
        }

        Assert.Equal(Enumerable.Range(1, 1600).Select(i => (long)i), sequences);
    }

    [Fact]
    public async Task FileLog_IsWrittenAndFlushedOnDispose()
    {
        using var directory = new TemporaryDirectory("logstore");
        var store = new LogStore(10, directory.DirectoryPath);
        store.Write(LogLevel.Warning, "Daemon", "written to disk");

        await store.DisposeAsync();

        var file = Assert.Single(Directory.GetFiles(directory.DirectoryPath, "daemon-*.log"));
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("WAR [Daemon] written to disk", text, StringComparison.Ordinal);
    }
}
