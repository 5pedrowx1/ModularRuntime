# Plugin development

A ModularRuntime plugin is a .NET 10 class library that references `ModularRuntime.Contracts`
(usually through the `ModularRuntime.Sdk` package), contains one public class implementing
`IPlugin` with a public parameterless constructor, and ships a `plugin.json` next to its entry
assembly. The runtime loads each plugin into its own collectible `AssemblyLoadContext` and gives
it a context with logging, cooperative shutdown, and the communication platform
([COMMUNICATION.md](COMMUNICATION.md)).

This is the reference for plugin API **0.4**. The tutorial is [GETTING_STARTED.md](GETTING_STARTED.md);
the tools and packages are in [SDK.md](SDK.md).

## Project setup

Create the project with the template (`dotnet new modularruntime-plugin -n MyPlugin`): it
references `ModularRuntime.Sdk`, sets `EnableDynamicLoading`, copies `plugin.json`, and enables
`RestorePackagesWithLockFile` (official review restores in locked mode). Without the template,
the equivalent project is:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Generates .deps.json/runtimeconfig.json and copies dependencies to the output. -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ModularRuntime.Sdk" Version="0.7.0" />
  </ItemGroup>
  <ItemGroup>
    <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

The samples in this repository reference the `ModularRuntime.Contracts` project instead, with
`<Private>false</Private>`: the runtime always supplies its own `ModularRuntime.Contracts`, so a
plugin never ships a copy (`dotnet modularruntime pack` excludes it). Plugins must not reference
`ModularRuntime.Engine`, `.Loader`, `.Daemon`, `.Ipc`, or any other host-side assembly
(`validate` reports `host_assembly_referenced`).

## The entry type

```csharp
using ModularRuntime.Contracts;

public sealed class MyPlugin : IPlugin
{
    private IPluginLogger? _logger;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;
        // Everything created through the context is owned by this instance and is
        // released automatically when it stops or fails to start.
        context.Events.Subscribe("example.greetings", (e, ct) =>
        {
            _logger.LogInformation($"Greeting from {e.Source}: {e.Payload.GetString("text")}");
            return ValueTask.CompletedTask;
        });
        context.Services.Register(
            new PluginServiceDefinition("my.echo", "Echo", new Version(1, 0), ["echo"]),
            (request, ct) => ValueTask.FromResult(PluginResponse.Success(request.Payload)));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

The loader requires the entry type (`entryType`) to be public, concrete, non-generic, to
implement `ModularRuntime.Contracts.IPlugin`, and to have a public parameterless constructor.

## The context: `IPluginContext`

| Member | Type | Purpose |
|---|---|---|
| `PluginId` | `PluginId` | the plugin's ID |
| `InstanceId` | `Guid` | this run's instance ID (new on every start) |
| `Descriptor` | `PluginDescriptor` | the validated manifest: `Name`, `Version`, `ApiVersion`, `EntryAssembly`, `EntryType`, `StartOrder`, `SharedContracts` |
| `Logger` | `IPluginLogger` | logging scoped to this instance |
| `StoppingToken` | `CancellationToken` | canceled when the runtime asks the plugin to stop |
| `Events` | `IPluginEventBus` | publish and subscribe |
| `Services` | `IPluginServiceRegistry` | JSON and binary services: register, discover, call |
| `TypedServices` | `ITypedServiceRegistry` | typed services over shared contract assemblies (API 0.4) |
| `Streams` | `IEventStreamRegistry` | create and subscribe to streams (API 0.4) |
| `Extensions` | `IExtensionRegistry` | extension points and contributions |
| `Resources` | `IResourceScope` | track your own disposables; list what the instance owns |
| `Owner` | `ResourceOwner` | this instance as a resource owner |

The host application gets the same surface through `PluginRuntime.Host` (`IRuntimeScope`).

## Lifecycle contract

- **Start:** a new instance is created in a new load context and `InitializeAsync` runs once.
  Resources created during initialization are active immediately (another plugin may call a
  service registered there). If it throws or is canceled, the plugin becomes `Failed` and
  everything it registered is released, after a best-effort rollback `ShutdownAsync`.
- **Stop:** the instance's scope is closed (no new resources or incoming calls), `StoppingToken`
  is canceled, in-flight handlers and typed calls are drained (up to `RuntimeLimits.DrainTimeout`,
  10 s), `ShutdownAsync` runs (it may still publish and call services), every owned resource is
  released, and the load context is asked to unload. Nothing needs to be undone manually.
- **Start again / restart:** a new instance in a new load context, after the installation (and,
  for managed packages, the signature and every file's SHA-256) is verified again. Never keep
  state in static fields expecting it to survive a restart.
- States: `Discovered → Validated → Loaded → Running → Stopping → Stopped` (or `Failed`);
  details in [LIFECYCLE.md](LIFECYCLE.md).

Rules that keep the runtime responsive:

- Honor the tokens: the one passed to `InitializeAsync`/`ShutdownAsync` is that operation's; the
  one passed to handlers and typed methods is canceled when the caller cancels or the plugin
  begins to stop; `StoppingToken` covers background work. Use `context.RunInBackground` (SDK) or
  track your own `IAsyncDisposable` with `context.Resources.Track(name, resource)` so shutdown
  waits for it.
- Do not block synchronously on asynchronous work inside lifecycle methods or handlers.
- Do not request your own stop (or dispose the runtime) from inside your handlers or lifecycle
  methods; the runtime answers `reentrant_lifecycle_operation` instead of deadlocking.
- Hand callbacks to other plugins through `context.TypedServices.RegisterCallback<TDelegate>(name, target)`
  and pass the registration's `Callback`: it is released with your instance (or when you dispose
  the registration) and holds nothing of you afterwards. A raw delegate over your code is
  refused at the boundary (`value_not_shared`) by default.

## Data across plugin boundaries

Only neutral data crosses between plugins and the host through JSON and binary mechanisms:
`JsonPayload` (validated JSON text), bytes, strings, numbers, `Guid`, `Version`, and the records of
`ModularRuntime.Contracts`. Never pass your own objects to another plugin through these; they
belong to your load context. `ModularRuntime.Sdk.PluginJson` converts your types:

```csharp
JsonPayload payload = PluginJson.ToPayload(new Greeting("Ada", 1));   // camelCase JSON, UTF-8
Greeting? value = payload.As<Greeting>();                             // or PluginJson.TryFromPayload
```

Typed services, callbacks, and typed streams pass objects directly, which is why their types, and
the types of everything they reference, must come from a **shared contract assembly**,
`ModularRuntime.Contracts`, or the framework. The runtime verifies this at every crossing and
refuses offenders by default; prefer closed contract types (sealed records and structs of
primitives, strings, and other closed types, typed arrays and lists), which need no inspection at
all. See [COMMUNICATION.md](COMMUNICATION.md#shared-values).

For plugins that are stopped and started often in long-running hosts, a source-generated
`JsonSerializerContext` avoids the reflection caches System.Text.Json keeps in the shared context
([LIFECYCLE.md](LIFECYCLE.md#unload-tracking)).

## Logging

`IPluginLogger.Log(LogLevel, string, Exception?)` with the extension methods `LogTrace`,
`LogDebug`, `LogInformation`, `LogWarning`, `LogError`, `LogCritical`. Entries carry the plugin ID
and instance ID and appear in the daemon log, on the desktop Logs page, and in
`modularruntime logs --plugin <id>`. Exceptions are converted to a text-only diagnostic before
they reach the host logger. Do not write to the console directly.

## Manifest schema (`plugin.json`)

A JSON object of at most 256 KiB; property names are case-sensitive and unknown properties are
rejected.

| Property | Required | Validation |
|---|---|---|
| `id` | yes | 1–128 lowercase ASCII letters, digits, `.`, `_`, `-`; separators cannot lead, trail, or repeat |
| `name` | yes | non-empty, at most 200 characters |
| `version` | yes | 2–4 numeric components (`1.0`, `1.0.0`, `1.2.3.4`); no pre-release labels |
| `apiVersion` | yes | exactly two numeric components; this runtime requires exactly `0.4` |
| `entryAssembly` | yes | relative path ending in `.dll`, at most 512 characters, no rooted path, drive/colon, NUL, or escape from the plugin directory; the file must exist |
| `entryType` | yes | exact fully qualified type name, at most 512 characters, no whitespace, comma, leading or trailing dot |
| `startOrder` | no | integer from -10000 to 10000 (default 0); bulk start runs ascending, ties in registration order; bulk stop runs in reverse |
| `sharedContracts` | no | at most 32 simple assembly names (letters, digits, `.`, `_`, `-`; no path, extension, or version; not `ModularRuntime.*`), each a `<name>.dll` next to the entry assembly, loaded into a shared context for typed services and streams |
| `description` | no | at most 1000 characters, no control characters except line feed; shown by the Store |
| `author` | no | at most 200 characters, no control characters; shown by the Store |

```json
{
  "id": "com.contoso.telemetry",
  "name": "Contoso Telemetry",
  "version": "1.0.0",
  "apiVersion": "0.4",
  "entryAssembly": "Contoso.Telemetry.dll",
  "entryType": "Contoso.Telemetry.TelemetryPlugin",
  "startOrder": 10,
  "sharedContracts": ["Contoso.Telemetry.Contracts"],
  "author": "Contoso",
  "description": "Records telemetry samples for other plugins."
}
```

Compatibility: the plugin API is experimental (0.x), so `apiVersion` must be exactly the
supported `major.minor` and the compiled entry assembly must reference `ModularRuntime.Contracts`
with the same `major.minor` (checked at discovery and at load, `contracts_assembly_mismatch`).
From API 1.0 the rule becomes "same major, minor ≤ supported". See [VERSIONING.md](VERSIONING.md).

## Installation layout

A plugin root contains one immediate subdirectory per installation; only subdirectories with a
`plugin.json` are considered, nested installations are not scanned, and later manifests with an
already-seen ID are reported as `duplicate_plugin_id`.

```text
plugins/
  com.contoso.telemetry/
    plugin.json
    Contoso.Telemetry.dll
    Contoso.Telemetry.deps.json
    Contoso.Telemetry.runtimeconfig.json
    Contoso.Telemetry.Contracts.dll      declared in sharedContracts: loaded into a shared context
    ModularRuntime.Sdk.dll               private dependency
    ...other private dependencies and native assets; no ModularRuntime.Contracts.dll...
```

Copy the **complete** build output: `AssemblyDependencyResolver` resolves dependencies from the
entry assembly's `.deps.json`. A dependency is private to the plugin's load context unless it is
`ModularRuntime.Contracts` (always the runtime's copy), a declared shared contract, or supplied by
the default context (the framework).

In the daemon, plugins are normally installed as **packages** (`dotnet modularruntime pack`, then
Store → Install from ZIP or `modularruntime plugins install-zip`), which the daemon verifies and
installs without a restart ([SDK.md](SDK.md), [PLUGIN_PACKAGE_FORMAT.md](PLUGIN_PACKAGE_FORMAT.md)).
A folder copied by hand into the plugin root is discovered at startup as *Unmanaged*: never
verified, never shown as verified, not updatable by the package manager. Packaged plugins must
not create files inside their installation directory (the next verification would report the
installation as modified): store data elsewhere.

## Error codes

Discovery (`PluginDiscoveryError.Code`):

| Code | Meaning |
|---|---|
| `plugin_directory_invalid` / `_not_found` / `_access_failed` | the root path is invalid, missing, or unreadable |
| `manifest_read_failed` | manifest missing, unreadable, or too large |
| `manifest_json_invalid` | invalid JSON, null document, or unknown property |
| `manifest_field_missing` / `manifest_field_invalid` | a required property is absent, or a value violates its rule |
| `api_version_incompatible` | `apiVersion` is not the supported one |
| `entry_assembly_path_invalid` / `entry_assembly_missing` | unsafe path, or the file does not exist |
| `shared_contract_missing` | a declared shared contract `.dll` is not in the installation |
| `duplicate_plugin_id` | another valid installation already uses the ID |

Load (`PluginLoadException`, `PluginLoadErrorCodes`):

| Code | Meaning |
|---|---|
| `entry_assembly_missing` / `assembly_load_failed` | the entry assembly disappeared or cannot be loaded |
| `contracts_assembly_mismatch` | missing or incompatible `ModularRuntime.Contracts` reference |
| `plugin_descriptor_mismatch` | the descriptor does not match the discovered candidate |
| `entry_type_not_found` / `entry_type_not_a_plugin` / `entry_type_not_instantiable` | the entry type problem |
| `plugin_instance_creation_failed` | the constructor failed or returned no instance |
| `factory_released` / `factory_already_consumed` | a factory used after release or twice |
| `installation_verification_failed` / `installation_revoked` | the package manager refused the load |
| `shared_contract_missing` / `shared_contract_invalid` | a declared contract file is missing or not a valid assembly of that name |
| `shared_contract_version_conflict` | the installation's copy is older than what the entry assembly references, or an older copy is already loaded for other plugins |
| `shared_contract_dependency` | the contract references an assembly that is not framework, `ModularRuntime.Contracts`, or another declared shared contract |

Engine and platform codes (`PluginErrorCodes`) are listed in
[COMMUNICATION.md](COMMUNICATION.md#error-codes); lifecycle results use `initialization_failed`,
`shutdown_failed`, `invalid_state`, `operation_canceled`, `resource_cleanup_failed`,
`release_failed`, `reentrant_lifecycle_operation`.

## Troubleshooting

- **`api_version_incompatible`** — this runtime accepts only `0.4`; rebuild against the current
  packages and update the manifest.
- **`manifest_json_invalid` for a property you added** — the reader rejects unknown fields; remove
  it.
- **`typed_service_contract_mismatch` when resolving a typed service** — one side did not declare
  the contract assembly in `sharedContracts` (or the host compiled its own copy); both plugins
  must declare it so the runtime loads it once.
- **`shared_contract_version_conflict`** — another running plugin brought an older copy of the
  contract; stop or update it, or start the plugin with the newest copy first. Keep contract
  changes additive within a major.
- **`shared_contract_dependency`** — the contract assembly references a private assembly; a
  contract must depend only on the framework, `ModularRuntime.Contracts`, and other shared
  contracts.
- **`extension_point_not_found` when contributing** — the point is declared by a host or another
  plugin that is absent or starts later; use `startOrder` or treat it as optional.
- **Plugin works in isolation but not when installed** — copy the entire build output, including
  `.deps.json` and the shared contract assemblies.
- **Unload does not complete** — collection is cooperative: do not keep threads running, static
  roots, or handlers that ignore cancellation; hand callbacks over through `RegisterCallback`;
  serialize your DTOs with your own `JsonSerializerOptions` instance.
- **`value_not_shared` or `value_not_verifiable` on a typed call or stream write** — the value,
  or something it references (a field, an element, a delegate target, a comparer), has a type
  private to a plugin, or the value is too large or too deep to verify within
  `RuntimeLimits.MaxValueScanReferences` / `MaxValueScanDepth`. The message names the path to the
  offending reference. Use contract types, typed collections, and `RegisterCallback`.
- **`typed_service_invalid_contract` at registration** — a signature of the contract (or callback
  delegate) uses a type that is not shared, or the contract type itself is private to the plugin
  (declare its assembly in `sharedContracts`).
