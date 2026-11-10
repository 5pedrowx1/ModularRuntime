# Developer SDK overview

Everything a C# developer needs to write, build, check, package, and submit a ModularRuntime
plugin, without knowing the engine, the loader, the daemon, or the IPC protocol.

| Piece | What it is | You use it to |
|---|---|---|
| `ModularRuntime.Contracts` (NuGet) | The plugin API: `IPlugin`, `IPluginContext`, events, services, extension points, resources, `JsonPayload` | implement the plugin (shared with the host at run time) |
| `ModularRuntime.Sdk` (NuGet) | Helpers over the contracts: typed JSON payloads, `PluginOperations`, typed `InvokeAsync`, `RunInBackground` | write less plumbing (copied into your package) |
| `ModularRuntime.Templates` (NuGet, `dotnet new`) | The `modularruntime-plugin` template with six kinds | start a project |
| `ModularRuntime.DevTools` (.NET tool) | `dotnet modularruntime validate · pack · inspect · prepare-submission · doctor` | check, package, inspect, submit |
| `ModularRuntime.Engine` + `ModularRuntime.Loader` (NuGet) | The runtime itself, for hosts that embed it | embed plugins in your own application |

```text
 your plugin project ──references──▶ ModularRuntime.Sdk ──▶ ModularRuntime.Contracts
        │                                                         ▲ shared at run time
        │ dotnet modularruntime pack                              │
        ▼                                                         │
  local .zip (unsigned) ──Install from ZIP──▶ daemon (Plugin Manager, Loader, Engine)
        │
        └─ dotnet modularruntime prepare-submission ──▶ review, rebuild, and signing by the administrator
```

## What stays separate

- **`ModularRuntime.Cli`** (`modularruntime …`) administers a running daemon over IPC.
- **`ModularRuntime.DevTools`** (`dotnet modularruntime …`) works on your project and packages
  on disk. It never needs the daemon, never runs plugin code, and never signs anything.
- Official packages are signed by the ModularRuntime administrator, offline, after review and a
  controlled rebuild, with tools that are not distributed. Anyone can verify the result:
  `dotnet modularruntime inspect` (packages) and `dotnet modularruntime registry-check` (catalog).

## Guides

1. [GETTING_STARTED.md](GETTING_STARTED.md): from `dotnet new` to a running plugin.
2. [PLUGIN_API.md](PLUGIN_API.md): the lifecycle, the context, logging, resources.
3. [EVENT_BUS_GUIDE.md](EVENT_BUS_GUIDE.md), [SERVICE_REGISTRY_GUIDE.md](SERVICE_REGISTRY_GUIDE.md),
   [EXTENSION_POINTS_GUIDE.md](EXTENSION_POINTS_GUIDE.md).
4. [PLUGIN_PACKAGING.md](PLUGIN_PACKAGING.md): validate, pack, inspect, diagnostics.
5. [PLUGIN_SUBMISSION_GUIDE.md](PLUGIN_SUBMISSION_GUIDE.md): getting a plugin into the official registry.
6. [VERSIONING.md](VERSIONING.md): product version and plugin API version.

Reference: [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) (manifest schema, installation layout,
error codes), [PLUGIN_SECURITY_MODEL.md](PLUGIN_SECURITY_MODEL.md).
