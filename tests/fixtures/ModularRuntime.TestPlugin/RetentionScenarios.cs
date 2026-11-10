using ModularRuntime.Contracts;

namespace ModularRuntime.TestPlugin;

/// <summary>
/// Keeps its own instance (and therefore its load context) reachable from a process-wide root
/// after shutdown: a misbehaving plugin. The host test releases the root with
/// <c>AppDomain.CurrentDomain.SetData(RetainingPlugin.RootKey + instanceId, null)</c>.
/// </summary>
public sealed class RetainingPlugin : IPlugin
{
    public const string RootKey = "ModularRuntime.TestPlugin.RetainingPlugin:";

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        AppDomain.CurrentDomain.SetData(RootKey + context.InstanceId.ToString("N"), this);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A record serialized with reflection-based System.Text.Json (as plugins commonly do).</summary>
public sealed record SerializedGreeting(string Name, int Count);

/// <summary>
/// Serializes and deserializes its own types with System.Text.Json, whose shared (default load
/// context) reflection caches then reference this plugin's types.
/// </summary>
public sealed class JsonUsingPlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var json = System.Text.Json.JsonSerializer.Serialize(new SerializedGreeting("plugin", 1));
        var back = System.Text.Json.JsonSerializer.Deserialize<SerializedGreeting>(json);
        context.Logger.LogInformation($"Round-tripped {back}.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
