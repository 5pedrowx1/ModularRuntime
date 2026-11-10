# Plugin Development

## Overview

A ModularRuntime plugin is a .NET 10 class library that:

1. references `ModularRuntime.Contracts`;
2. contains a public, concrete, non-generic class that implements `IPlugin`;
3. exposes a public parameterless constructor;
4. ships a valid `plugin.json` next to its entry assembly;
5. copies all runtime dependencies into the plugin installation directory.

The plugin is loaded into a private collectible `AssemblyLoadContext`. The
runtime always shares its own `ModularRuntime.Contracts` assembly with the
plugin, even if the plugin output contains a copy.

Since v0.2 the context also gives the plugin the extensibility platform: an event
bus, a service registry, extension points, and resource ownership. See
[V0.2_EXTENSIBILITY.md](V0.2_EXTENSIBILITY.md) for the complete reference. Plugins
communicate only through these contracts and JSON payloads, never through
references to each other's assemblies.

## Project setup

> **Recommended:** create the project with the template
> (`dotnet new modularruntime-plugin -n MyPlugin`, see [GETTING_STARTED.md](GETTING_STARTED.md)).
> It references the `ModularRuntime.Sdk` NuGet package. Package and check it with
> `dotnet modularruntime validate` / `pack` ([PLUGIN_PACKAGING.md](PLUGIN_PACKAGING.md)). The
> project-reference setup below is what the samples in this repository use.

The simplest project file inside this repository is:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Generates .deps.json/runtimeconfig.json and copies dependencies. -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <!-- Private=false: the runtime supplies the shared contracts; do not ship a copy. -->
    <ProjectReference Include="..\..\src\ModularRuntime.Contracts\ModularRuntime.Contracts.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>

  <ItemGroup>
    <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

Reference the contracts project (or a contracts assembly with the same identity)
and target `net10.0`. The runtime does not load a plugin that targets another
framework unless that framework's assemblies are resolvable in the private load
context.

## Plugin implementation

```csharp
using System.Text.Json.Nodes;
using ModularRuntime.Contracts;

namespace MyPlugin;

public sealed class MyPlugin : IPlugin
{
    private IPluginLogger? _logger;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;

        // Everything created through the context is owned by this instance and is
        // released automatically when it stops or fails.
        context.Events.Subscribe("example.greetings", (e, ct) =>
        {
            _logger.LogInformation($"Greeting from {e.Source}: {e.Payload.GetString("text")}");
            return ValueTask.CompletedTask;
        });

        context.Services.Register(
            new PluginServiceDefinition("my.echo", "Echo", new Version(1, 0), ["echo"]),
            (request, ct) => ValueTask.FromResult(PluginResponse.Success(request.Payload)));

        _logger.LogInformation($"Started instance {context.InstanceId}.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        // No manual unsubscribe/unregister is needed.
        _logger?.LogInformation("Shutting down.");
        return Task.CompletedTask;
    }
}
```

Requirements enforced by the loader:

- the entry type is public;
- it is a concrete class, not an interface or abstract type;
- it has no generic parameters;
- it has a public parameterless constructor;
- it implements `ModularRuntime.Contracts.IPlugin`.

## Manifest schema

`plugin.json` must be a JSON object of at most 256 KiB. Property names are
case-sensitive and unknown properties are rejected.

| Property | Required | Validation |
| --- | --- | --- |
| `id` | yes | 1–128 lowercase ASCII letters, digits, `.`, `_`, `-`; separators cannot lead, trail, or repeat |
| `name` | yes | non-empty, at most 200 characters |
| `version` | yes | 2–4 numeric components, for example `1.0`, `1.0.0`, or `1.2.3.4` |
| `apiVersion` | yes | exactly two numeric components; this runtime requires exactly `0.3` |
| `entryAssembly` | yes | relative path ending in `.dll`, at most 512 characters, no rooted path, drive/colon, NUL, or escape from the plugin directory; the file must exist |
| `entryType` | yes | exact fully qualified type name, at most 512 characters, no whitespace, comma, leading dot, or trailing dot |
| `startOrder` | no | integer from -10000 to 10000 (default 0); bulk start runs in ascending order, ties in registration order; stop runs in reverse |
| `description` | no | at most 1000 characters, no control characters except line feed; shown by the package manager (v0.4) |
| `author` | no | at most 200 characters, no control characters; shown by the package manager (v0.4) |

Example:

```json
{
  "id": "com.example.hello",
  "name": "Hello Plugin",
  "version": "1.0.0",
  "apiVersion": "0.3",
  "entryAssembly": "HelloPlugin.dll",
  "entryType": "HelloPlugin.HelloPlugin",
  "startOrder": 0
}
```

Compatibility rules for v0.2:

- plugin version is informational metadata; pre-release labels are not accepted;
- the plugin API is experimental (0.x): `apiVersion` must be exactly `0.3`,
  because any 0.x minor may contain breaking changes (from 1.0 onward the rule
  will be "same major, minor less than or equal to the supported minor");
- the contracts assembly referenced by the compiled entry assembly must have the
  same major.minor (`0.3`); this is checked again at load time;
- plugins built for v0.1 (API `1.0`) must be updated and rebuilt.

## Installation layout

A plugin root contains one immediate subdirectory per installation. Only
subdirectories that contain `plugin.json` are considered; nested installation
directories are not scanned.

```text
plugins/
  com.example.hello/
    plugin.json
    HelloPlugin.dll
    HelloPlugin.deps.json
    HelloPlugin.runtimeconfig.json
    ...private dependencies (no ModularRuntime.Contracts.dll needed)...
```

The directory name does not have to equal the manifest ID, but using the ID is
recommended. Discovery sorts directory names and rejects later manifests that
reuse an already-seen ID; the first valid manifest wins and the duplicate is
reported as an error.

Copy the **complete** build output into the installation directory. In
particular, keep:

- the entry assembly;
- `plugin.json`;
- `.deps.json` and `.runtimeconfig.json` generated by
  `EnableDynamicLoading`;
- private managed dependencies and any native assets declared by the plugin.

`AssemblyDependencyResolver` uses the entry assembly and its `.deps.json` to
resolve dependencies from this directory. A dependency is private to the plugin
load context unless it is `ModularRuntime.Contracts` or is supplied by the
default context through normal assembly resolution.

## Build and stage

Build and copy manually:

```powershell
dotnet build ./samples/HelloPlugin/HelloPlugin.csproj -c Release
Copy-Item ./samples/HelloPlugin/bin/Release/net10.0/* ./plugins/com.example.hello/ -Recurse
```

Or stage the included samples with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/prepare-plugins.ps1 -Configuration Release
```

The script builds every sample folder that contains a `plugin.json` (all plugin
samples, not `EmbeddedExample`), reads each manifest ID, and replaces
`plugins/<id>/` with the complete sample output.

### Running a plugin in the daemon

Since v0.3 plugins run inside `ModularRuntime.Daemon`. Since v0.4 the normal way to install
a plugin is a **package**: zip the complete build output (with `plugin.json` at the root) and
install it with *Install from ZIP* in the desktop app or `ModularRuntime.Cli plugins
install-zip <file.zip>`. The daemon verifies and installs it without a restart; without an
official signature it is installed as "Local, unverified" after an explicit confirmation and
is never started automatically. Official packages are built and signed by the ModularRuntime
administrator from your source code ([PLUGIN_SUBMISSION_POLICY.md](PLUGIN_SUBMISSION_POLICY.md)); the
package format is in [PLUGIN_PACKAGE_FORMAT.md](PLUGIN_PACKAGE_FORMAT.md).

Rules for packaged plugins: every file must be inside the package (no files created next to
the plugin at runtime: store data elsewhere, or the next verification reports the
installation as modified), file names must follow the package path rules, and the package must
not contain `integrity.json`/`integrity.sig` at its root unless they are the real signature
metadata.

Copying a folder into the plugin root by hand still works (the daemon discovers it at
startup, `<profile>\plugins` by default or `--plugins`/`pluginDirectory`), but such a plugin is
"Unmanaged": it is not verified, cannot be updated or removed by the package manager, and is
never shown as verified. Then use `plugins list`, `plugins start <id>`, and
`logs --plugin <id>` from the CLI, or the Plugins and Logs pages of the desktop
app. Everything your plugin logs through `context.Logger` appears in the daemon
log with its plugin id.

## Lifecycle contract

- `InitializeAsync` runs once per started instance. Do not start long-running
  work that ignores cancellation.
- `ShutdownAsync` is called at most once for a successfully initialized
  instance. It is also called as a best-effort rollback after a failed
  initialization.
- `IPluginContext.StoppingToken` is canceled when the runtime requests shutdown.
  Observe it in background tasks, timers, and I/O loops.
- The `CancellationToken` passed to `InitializeAsync`/`ShutdownAsync` represents
  the current operation. Honor it cooperatively.
- Expected cancellation should normally use `OperationCanceledException` with
  the supplied token.
- Do not block synchronously on async work inside lifecycle methods.

- Handlers (events, services, contributions) receive a token that is canceled
  when the caller cancels or when the plugin begins to stop. Honor it: stopping
  waits for in-flight handlers (up to `RuntimeLimits.DrainTimeout`, default 10 s)
  before `ShutdownAsync` runs.
- Do not request your own stop (or dispose the runtime) from inside your handlers
  or lifecycle methods; the runtime rejects it with
  `reentrant_lifecycle_operation` instead of deadlocking.

If initialization throws, the plugin becomes `Failed`; the runtime releases every
resource it created (subscriptions, services, extension points, contributions,
tracked objects) and calls its best-effort rollback shutdown. If shutdown throws, the plugin also becomes
`Failed`. A plugin in `Stopped` or `Failed` is terminal for that registration.

See [LIFECYCLE.md](LIFECYCLE.md) for the complete state machine.

## Logging

Use `IPluginContext.Logger`:

```csharp
context.Logger.LogInformation("Background worker started.");
context.Logger.Log(
    LogLevel.Warning,
    "A recoverable problem occurred.",
    exception);
```

The runtime logger adds the plugin ID and instance ID to the scope. Plugins must
not write directly to the console if they want host-controlled logging.

## Error codes

Discovery errors are returned as `PluginDiscoveryError.Code`:

| Code | Meaning |
| --- | --- |
| `plugin_directory_invalid` | Root path is syntactically invalid |
| `plugin_directory_not_found` | Root directory does not exist |
| `plugin_directory_access_failed` | Root could not be enumerated |
| `manifest_read_failed` | Manifest missing, unreadable, or too large |
| `manifest_json_invalid` | Invalid JSON or null document |
| `manifest_field_missing` | Required property absent or empty |
| `manifest_field_invalid` | Value violates its format or limits |
| `api_version_incompatible` | Requested API is outside the supported range |
| `entry_assembly_path_invalid` | Unsafe, absolute, or escaping entry path |
| `entry_assembly_missing` | Declared entry assembly file does not exist |
| `duplicate_plugin_id` | Another valid installation already registered this ID |

Load errors are raised as `PluginLoadException` with
`PluginLoadErrorCodes`:

| Code | Meaning |
| --- | --- |
| `entry_assembly_missing` | Entry assembly disappeared before loading |
| `assembly_load_failed` | Assembly could not be loaded |
| `contracts_assembly_mismatch` | Missing or incompatible contracts reference |
| `plugin_descriptor_mismatch` | Descriptor does not match the discovered candidate |
| `entry_type_not_found` | Entry type was not found in the assembly |
| `entry_type_not_a_plugin` | Type does not implement `IPlugin` |
| `entry_type_not_instantiable` | Type is not public/concrete/non-generic or lacks a public parameterless constructor |
| `plugin_instance_creation_failed` | Constructor failed or returned no instance |
| `factory_released` | Factory used after release |
| `factory_already_consumed` | A second instance was requested from the same factory |

Engine failures are returned in `PluginOperationResult.ErrorCode` using
`PluginErrorCodes`, for example `initialization_failed`, `shutdown_failed`,
`invalid_state`, `operation_canceled`, `resource_cleanup_failed`, and
`reentrant_lifecycle_operation`. Platform operations use the same class for
`PluginRuntimeException.ErrorCode` and for event/invocation results (for example
`scope_closed`, `duplicate_service`, `service_not_found`, `service_removed`,
`extension_point_not_found`, `payload_too_large`); see
[V0.2_EXTENSIBILITY.md](V0.2_EXTENSIBILITY.md#error-model).

## Troubleshooting

- **`manifest_field_invalid` for `version`** — use two to four numeric
  components; values such as `1.0.0-beta` are not supported.
- **`api_version_incompatible`** — v0.5 accepts only `0.3`; rebuild against the
  current contracts and update the manifest.
- **Unknown JSON property** — remove it or update to a runtime that documents it;
  the manifest reader rejects unknown fields.
- **`extension_point_not_found` when contributing** — the point is declared by a
  host or another plugin that is not present or starts later; use `startOrder` or
  handle the error as optional functionality.
- **`contracts_assembly_mismatch`** — build the plugin against the contracts
  assembly shipped by this runtime.
- **`entry_type_not_found`** — `entryType` is case-sensitive and must include the
  namespace.
- **Plugin works in isolation but not when staged** — copy the entire build
  output, not only the plugin DLL, and keep the `.deps.json` file.
- **Unload does not complete immediately** — collection is cooperative. The
  runtime releases everything it manages, but the plugin must not keep threads
  running, register static events on shared types, or leave handlers running
  that ignore cancellation. Serialize your DTOs with your own
  `JsonSerializerOptions` instance rather than shared static caches.
