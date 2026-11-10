using ModularRuntime.Contracts;
using ModularRuntime.Loader;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Integration.Tests;

/// <summary>
/// Regression for a race in <see cref="PluginRuntime.StartAsync"/>. Starting a stopped plugin renews
/// its registration: unregister (the engine removes the slot, releases the old factory and load
/// context, and logs), then register a new factory. A concurrent start that looked at the plugin in
/// that window found it unregistered, skipped the renewal gate, and asked the engine directly, which
/// answered <c>plugin_not_found</c> for a plugin that exists. Under load (the release of the old load
/// context takes longer) the window was wide enough to make
/// <c>PluginRestartTests.ConcurrentStartsOfAStoppedPlugin_CreateExactlyOneInstance</c> fail
/// intermittently. The window is held open here through the runtime's own logger, so the race is
/// reproduced deterministically without a test hook in the product.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in IAsyncLifetime.DisposeAsync, which xUnit calls.")]
public sealed class PluginRestartConcurrencyTests : IAsyncLifetime
{
    private static readonly PluginId HelloId = PluginId.Parse("com.example.hello");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TemporaryDirectory _temporary = new("restart-race");
    private readonly WindowLogger _logger = new();
    private PluginRuntime _runtime = null!;

    public async Task InitializeAsync()
    {
        var root = Path.Combine(_temporary.DirectoryPath, "plugins");
        Directory.CreateDirectory(root);
        StagedPlugins.CopyPlugin(root, "HelloPlugin");
        _runtime = new PluginRuntime(new PluginRuntimeOptions { PluginDirectory = root, Logger = _logger });
        await _runtime.DiscoverAsync();
    }

    public async Task DisposeAsync()
    {
        _logger.Release();
        await _runtime.DisposeAsync();
        _temporary.Dispose();
    }

    [Fact]
    public async Task AStartDuringTheRenewalOfAStoppedPlugin_WaitsForIt_AndNeverSeesThePluginMissing()
    {
        Assert.True((await _runtime.StartAsync(HelloId)).IsSuccess);
        Assert.True((await _runtime.StopAsync(HelloId)).IsSuccess);

        // First start: renews the registration and is held inside the window (slot removed, new one not registered yet).
        _logger.HoldNext($"Plugin '{HelloId}' unregistered.");
        var first = Task.Run(() => _runtime.StartAsync(HelloId));
        Assert.True(_logger.Held.Wait(Timeout), "The renewal never reached the unregistered window.");
        Assert.Null(_runtime.GetPlugin(HelloId)); // the window: the plugin is not registered at this moment

        // Second start, concurrently, while the plugin is momentarily unregistered.
        var second = Task.Run(() => _runtime.StartAsync(HelloId));
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(500)));
        _logger.Release();
        var results = await Task.WhenAll(first, second).WaitAsync(Timeout);

        Assert.True(results[0].IsSuccess, results[0].Message);
        Assert.False(results[1].IsSuccess);
        Assert.Equal(PluginErrorCodes.InvalidState, results[1].ErrorCode);
        Assert.Equal(PluginState.Running, _runtime.GetPlugin(HelloId)!.State);
    }

    [Fact]
    public async Task ConcurrentStartsOfAStoppedPlugin_KeepTheirInvariant_OverRepeatedRounds()
    {
        Assert.True((await _runtime.StartAsync(HelloId)).IsSuccess);
        var unexpected = new List<string>();
        for (var round = 1; round <= 50; round++)
        {
            Assert.True((await _runtime.StopAsync(HelloId)).IsSuccess, $"round {round}: stop failed");
            using var go = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                go.Wait();
                return _runtime.StartAsync(HelloId);
            })).ToList();
            go.Set();
            var results = await Task.WhenAll(tasks).WaitAsync(Timeout);

            if (results.Count(r => r.IsSuccess) != 1)
            {
                unexpected.Add($"round {round}: {results.Count(r => r.IsSuccess)} successes");
            }

            unexpected.AddRange(results.Where(r => !r.IsSuccess && r.ErrorCode != PluginErrorCodes.InvalidState).Select(r => $"round {round}: {r.ErrorCode} — {r.Message}"));
        }

        Assert.True(unexpected.Count == 0, string.Join('\n', unexpected.Take(10)));
    }

    /// <summary>A logger that can hold the thread that writes one given message, keeping a window open.</summary>
    private sealed class WindowLogger : IRuntimeLogger, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(initialState: true);
        private string? _hold;

        public ManualResetEventSlim Held { get; } = new();

        public void HoldNext(string message)
        {
            _release.Reset();
            Held.Reset();
            Volatile.Write(ref _hold, message);
        }

        public void Release() => _release.Set();

        public void Log(LogEntry entry)
        {
            if (Volatile.Read(ref _hold) is { } hold && entry.Message == hold && Interlocked.CompareExchange(ref _hold, null, hold) == hold)
            {
                Held.Set();
                _release.Wait(Timeout);
            }
        }

        public void Dispose()
        {
            _release.Dispose();
            Held.Dispose();
        }
    }
}
