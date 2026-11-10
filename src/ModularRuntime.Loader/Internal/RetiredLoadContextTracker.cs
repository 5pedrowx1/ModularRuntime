using System.Reflection;
using System.Reflection.Metadata;
using ModularRuntime.Contracts;

namespace ModularRuntime.Loader.Internal;

/// <summary>
/// Remembers the load contexts a plugin left behind (unregistration, previous runs of a restarted
/// plugin) through weak references only, until each one is observed collected.
/// </summary>
/// <remarks>
/// <para>
/// An entry is removed only when its context is dead: a wait that times out or is canceled keeps
/// every live entry, so waiting again later still reports it. Dead entries of every plugin are
/// pruned whenever a context is tracked or queried, so a long-running process that restarts
/// plugins many times does not accumulate records of contexts that were already collected.
/// </para>
/// <para>All members are thread-safe. No lock is held while waiting.</para>
/// </remarks>
internal sealed class RetiredLoadContextTracker
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly Lock _sync = new();
    private readonly Dictionary<PluginId, List<WeakReference>> _pending = [];

    /// <summary>Number of live references tracked for every plugin (diagnostics and tests).</summary>
    public int TrackedCount
    {
        get
        {
            lock (_sync)
            {
                PruneAll();
                return _pending.Values.Sum(list => list.Count);
            }
        }
    }

    public void Track(PluginId pluginId, WeakReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_sync)
        {
            PruneAll();
            if (!reference.IsAlive)
            {
                return;
            }

            if (!_pending.TryGetValue(pluginId, out var references))
            {
                _pending[pluginId] = references = [];
            }

            references.Add(reference);
        }
    }

    /// <summary>Number of retired load contexts of the plugin that are still alive.</summary>
    public int GetPendingCount(PluginId pluginId)
    {
        lock (_sync)
        {
            if (!_pending.TryGetValue(pluginId, out var references))
            {
                return 0;
            }

            references.RemoveAll(r => !r.IsAlive);
            if (references.Count == 0)
            {
                _pending.Remove(pluginId);
            }

            return references.Count;
        }
    }

    /// <summary>
    /// Waits until no retired load context of the plugin is alive. Returns <see langword="false"/>
    /// at the timeout; throws <see cref="OperationCanceledException"/> when canceled. In both cases
    /// the live contexts stay tracked.
    /// </summary>
    public async Task<bool> WaitAsync(
        PluginId pluginId,
        TimeSpan timeout,
        PluginUnloadWait mode,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        var started = timeProvider.GetTimestamp();
        var cachesFlushed = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetPendingCount(pluginId) == 0)
            {
                return true;
            }

            if (mode == PluginUnloadWait.InduceCollection)
            {
                // Only on explicit request (for example before replacing a plugin's files):
                // collectible contexts are otherwise collected whenever the GC next runs.
                if (!cachesFlushed)
                {
                    SharedCaches.Flush();
                    cachesFlushed = true;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (GetPendingCount(pluginId) == 0)
                {
                    return true;
                }
            }

            var remaining = timeout - timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private void PruneAll()
    {
        List<PluginId>? empty = null;
        foreach (var (pluginId, references) in _pending)
        {
            references.RemoveAll(r => !r.IsAlive);
            if (references.Count == 0)
            {
                (empty ??= []).Add(pluginId);
            }
        }

        if (empty is not null)
        {
            foreach (var pluginId in empty)
            {
                _pending.Remove(pluginId);
            }
        }
    }
}

/// <summary>
/// Caches of shared framework assemblies that are keyed by type and can keep a plugin's types (and
/// therefore its collectible load context) reachable after the plugin stopped.
/// </summary>
/// <remarks>
/// System.Text.Json is loaded once, in the default context, and caches reflection metadata and
/// reflection-emitted member accessors for every type it serializes, including plugin types. Those
/// caches are evicted only lazily, the next time the serializer is used. Flushing uses the cache
/// handler System.Text.Json declares for Hot Reload through the public
/// <see cref="MetadataUpdateHandlerAttribute"/> (its static <c>ClearCache(Type[]?)</c> method).
/// The caches are rebuilt on demand; the cost is a slower next serialization.
/// </remarks>
internal static class SharedCaches
{
    public static void Flush()
    {
        foreach (var attribute in typeof(System.Text.Json.JsonSerializer).Assembly.GetCustomAttributes<MetadataUpdateHandlerAttribute>())
        {
            try
            {
                attribute.HandlerType
                    .GetMethod("ClearCache", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(Type[])])
                    ?.Invoke(null, [null]);
            }
            catch (TargetInvocationException)
            {
                // Best effort: an unflushed cache only delays collection until its own eviction.
            }
        }
    }
}
