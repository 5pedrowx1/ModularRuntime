# Plugin API (0.3)

The API a plugin uses is `ModularRuntime.Contracts` (namespace `ModularRuntime.Contracts`) plus
the optional helpers in `ModularRuntime.Sdk` (namespace `ModularRuntime.Sdk`). The types below
exist as listed. Everything else (engine, loader, daemon) is host-side and not available to
plugins.

## Lifecycle: `IPlugin`

```csharp
public sealed class MyFirstPlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation($"{context.Descriptor.Name} {context.Descriptor.Version} started.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- The entry type (`entryType` in `plugin.json`) must be a public, non-abstract class with a public
  parameterless constructor implementing `IPlugin`.
- **Start:** the runtime creates a new instance in a new collectible load context and calls
  `InitializeAsync`. If it throws or is canceled, the plugin is *Failed* and everything it
  registered is removed.
- **Stop:** `context.StoppingToken` is canceled, then `ShutdownAsync` runs. After it returns, the
  runtime releases every resource the instance owns and asks the load context to unload.
- **Start again / Restart:** a new instance in a new load context, after the installation (and
  for managed packages, the signature and every file's SHA-256) is verified again. Never keep
  state in static fields expecting it to survive a restart.
- States: `Discovered → Validated → Loaded → Running → Stopping → Stopped` (or `Failed`).

## The context: `IPluginContext`

| Member | Type | Purpose |
|---|---|---|
| `PluginId` | `PluginId` | the plugin's ID |
| `InstanceId` | `Guid` | this run's instance ID (new on every start) |
| `Descriptor` | `PluginDescriptor` | *(API 0.3)* the validated manifest: `Name`, `Version`, `ApiVersion`, `EntryAssembly`, `EntryType`, `StartOrder` |
| `Logger` | `IPluginLogger` | logging scoped to this instance |
| `StoppingToken` | `CancellationToken` | canceled when the runtime asks the plugin to stop |
| `Events` | `IPluginEventBus` | publish and subscribe ([EVENT_BUS_GUIDE.md](EVENT_BUS_GUIDE.md)) |
| `Services` | `IPluginServiceRegistry` | register and call services ([SERVICE_REGISTRY_GUIDE.md](SERVICE_REGISTRY_GUIDE.md)) |
| `Extensions` | `IExtensionRegistry` | extension points and contributions ([EXTENSION_POINTS_GUIDE.md](EXTENSION_POINTS_GUIDE.md)) |
| `Resources` | `IResourceScope` | track your own disposables; list what the instance owns |
| `Owner` | `ResourceOwner` | this instance as a resource owner |

## Logging

`IPluginLogger.Log(LogLevel, string, Exception?)` with the extension methods `LogTrace`,
`LogDebug`, `LogInformation`, `LogWarning`, `LogError`, `LogCritical`. Entries carry the plugin ID
and instance ID and appear in the daemon log, on the desktop Logs page, and in `modularruntime logs`.

## Ownership and cleanup

Every subscription, service, extension point, contribution, and tracked resource created through
the context belongs to the instance and is removed automatically when it stops or fails, even if
`ShutdownAsync` throws. Track your own disposables so the runtime awaits them during stop:

```csharp
context.Resources.Track("cache", myAsyncDisposable);
```

The SDK's `context.RunInBackground(name, work)` (in `ModularRuntime.Sdk`) runs a background loop
with the stopping token and tracks it, so shutdown waits for it to observe cancellation.

## Data across plugin boundaries

Only neutral data crosses between plugins and the host: `JsonPayload` (validated JSON text),
strings, numbers, `Guid`, `Version`, and the records of the contracts. Never pass your own objects
to another plugin. They belong to your load context. Serialize them instead.

`ModularRuntime.Sdk.PluginJson` converts your types:

```csharp
JsonPayload payload = PluginJson.ToPayload(new Greeting("Ada", 1));   // camelCase JSON
Greeting? value = payload.As<Greeting>();                             // or PluginJson.TryFromPayload
```

Pass your own `JsonSerializerOptions` to any SDK method to control serialization. For plugins
that are stopped and started often in long-running hosts, a source-generated
`JsonSerializerContext` (`new JsonSerializerOptions { TypeInfoResolver = MyContext.Default }`)
avoids the reflection caches System.Text.Json keeps in the shared context (see
[LIFECYCLE.md](LIFECYCLE.md#unload-tracking)).

## Errors

Runtime operations return results (`PluginOperationResult`, `EventPublishResult`,
`PluginInvocationResult`, `ExtensionInvocationResult`) with `Status`, `ErrorCode`
(`PluginErrorCodes`), `Message`, and a text-only `Diagnostic`. Invalid arguments (for example an
invalid topic name) throw `ArgumentException`; registering on a stopped instance throws
`PluginRuntimeException` (`scope_closed`).

## Names

Plugin IDs, topics, service IDs, extension point IDs, contribution IDs, and operation names use
`RuntimeName` rules: 1–128 characters, lowercase ASCII letters and digits with `.`, `_`, `-`
separators, never leading, trailing, or doubled. Example: `com.contoso.reports`.
