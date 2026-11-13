# Developer SDK: packages, templates, and the `dotnet modularruntime` tool

Everything a C# developer needs to write, build, check, package, and submit a ModularRuntime
plugin, without knowing the engine, the loader, the daemon, or the IPC protocol.

| Piece | What it is | You use it to |
|---|---|---|
| `ModularRuntime.Contracts` (NuGet) | The plugin API: `IPlugin`, `IPluginContext`, events, services (JSON, binary, typed), streams, extension points, resources, `JsonPayload` | implement the plugin (shared with the host at run time; never packaged) |
| `ModularRuntime.Sdk` (NuGet) | Helpers over the contracts (below) | write less plumbing (copied into your package) |
| `ModularRuntime.Templates` (NuGet, `dotnet new`) | The `modularruntime-plugin` template, eleven kinds | start a project |
| `ModularRuntime.DevTools` (.NET tool) | `dotnet modularruntime validate · pack · inspect · prepare-submission · registry-check · doctor` | check, package, inspect, submit |
| `ModularRuntime.Engine` + `ModularRuntime.Loader` (NuGet) | The runtime itself, for hosts that embed it | embed plugins in your own application |

```text
 your plugin project ──references──▶ ModularRuntime.Sdk ──▶ ModularRuntime.Contracts
        │                                                         ▲ shared at run time
        │ dotnet modularruntime pack                              │
        ▼                                                         │
  local .zip (unsigned) ──Install from ZIP──▶ daemon (Plugin Manager, Loader, Engine)
        │
        └─ dotnet modularruntime prepare-submission ──▶ review, controlled rebuild, and signing by the administrator
```

ModularRuntime is pre-release and its packages are not on nuget.org: `scripts/pack-sdk.ps1`
writes them to `artifacts/nuget` (version 0.7.0), to be used as a local NuGet source. The tool and
the template are installed from that folder ([GETTING_STARTED.md](GETTING_STARTED.md)).

What stays separate: `ModularRuntime.Cli` (`modularruntime …`) administers a running daemon over
IPC; `ModularRuntime.DevTools` (`dotnet modularruntime …`) works on your project and packages on
disk, never needs the daemon, never runs plugin code, and never signs anything.

## `ModularRuntime.Sdk`

Helpers only; no second engine. Everything registered through the context is owned by the plugin
instance and removed when it stops.

| Helper | Purpose |
|---|---|
| `PluginJson.ToPayload<T>` / `FromPayload<T>` / `TryFromPayload<T>` / `payload.As<T>()` | convert your types to and from `JsonPayload` (camelCase web defaults, UTF-8; pass your own `JsonSerializerOptions` or a source-generated context) |
| `context.Events.PublishAsync<T>(topic, data)` / `Subscribe<T>(topic, handler)` | typed events |
| `PluginOperations` + `context.Services.Register(id, name, version, operations)` | route JSON service (or contribution) operations to typed handlers; `operation_not_supported`, `invalid_payload`, and `PluginOperationException(code, message)` answered for you |
| `context.Services.InvokeAsync<TRequest, TResponse>(id, operation, request)` / `context.Extensions.InvokeAsync<…>` / `result.ToTyped<T>()` | typed calls returning `PluginCallResult<T>` (a missing or stopped service is a failed result, never an exception) |
| `BinaryOperations` + `context.Services.RegisterBinary(id, name, version, operations, maxRequestBytes?, maxResponseBytes?)` | route binary service operations |
| `context.Consume<T>(streamId, handler, options?)` / `ConsumeJson<T>` | subscribe and read a stream in the background until it completes or the plugin stops (`null` when the stream does not exist) |
| `stream.WriteJsonAsync<T>(item)` | write typed items to a `JsonPayload` stream |
| `context.RunInBackground(name, work)` | background work with `StoppingToken`, tracked so shutdown waits for it |

Typed services need no helper: `context.TypedServices.Register<IContract>(definition, instance)`
and `Resolve<IContract>(id)` work on the contract interface directly, provided the contract
assembly is declared in `plugin.json` (`sharedContracts`) by every plugin that uses it. Values
that cross must be shared (contract, `ModularRuntime.Contracts`, framework types, all the way
down) and callbacks are handed over through `context.TypedServices.RegisterCallback<TDelegate>(name, target)`
([COMMUNICATION.md](COMMUNICATION.md#typed-services)).

## Templates

`dotnet new modularruntime-plugin -n <Name> [--kind <kind>] [options]` creates a project that
references `ModularRuntime.Sdk`, a `plugin.json`, a README, and a working entry type.

| `--kind` | Demonstrates | Options |
|---|---|---|
| `basic` (default) | lifecycle, context, logger, background work | |
| `event-publisher` / `event-subscriber` | typed JSON events on a topic | `--topic` |
| `service-provider` / `service-consumer` | a JSON service with typed operations; calling it and handling its absence | `--service-id` |
| `extension` | contributing to a host extension point | `--extension-point` |
| `typed-provider` / `typed-consumer` | a typed service and a typed stream over a shared contract project (`Contracts/`, declared in `plugin.json`); provider and consumer must use the same `--contract-assembly` | `--service-id`, `--stream-id`, `--contract-assembly` (default `Shared.Contracts`) |
| `binary-provider` | a binary service with fresh and pooled responses | `--service-id` |
| `stream-publisher` / `stream-subscriber` | a JSON stream written every second; a subscriber with a bounded buffer | `--stream-id` |

Common options: `--plugin-id` (default `com.example.<project name>`), `--plugin-name`,
`--plugin-author`, `--sdk-version`, `--no-restore`. Option values are not validated by
`dotnet new`: an invalid identifier is reported by `validate` (plugin ID) or at run time
(`invalid_identifier`); `--contract-assembly` must be identifier-safe segments separated by dots
(it is also the contract's namespace).

The typed kinds generate the contract as a subproject `Contracts/<assembly>.csproj` for a
self-contained start; real provider and consumer plugins share one contract project or package.

## `dotnet modularruntime`

```text
dotnet modularruntime validate [--project <path>] [-c <config>] [--build] [-v]
dotnet modularruntime pack [--project <path>] [-c <config>] [-o <file.zip|dir>] [--no-build] [--include-symbols] [-v]
dotnet modularruntime inspect <package.zip> [--test-trust-store <file>]
dotnet modularruntime prepare-submission [--project <path>] [--source-root <dir>] [-o <dir>] [--no-package]
                                         [--license <spdx>] [--source-repository <https url>] [--source-commit <sha>]
dotnet modularruntime registry-check <registry.json> [--previous <file>] [--packages-root <dir>] [--test-trust-store <file>]
dotnet modularruntime doctor [--project <path>]
dotnet modularruntime --help | --version
```

Exit codes: `0` success, `1` validation, build, or package failure, `2` usage error, `130`
canceled (Ctrl+C).

### validate

Static checks, using the runtime's own code where a rule exists:

| Check | Rule source |
|---|---|
| `plugin.json` exists, is valid JSON, has no unknown fields, and its fields are valid (ID, name, version, API version, entry assembly path, entry type, start order, shared contracts, author, description) | the Loader's manifest reader (`PluginLoader.ValidateManifestAsync`) |
| entry assembly equals the project's `AssemblyName.dll`; target framework is `net10.0`; references `ModularRuntime.Sdk` or `.Contracts`; no host-side references; `EnableDynamicLoading`; `plugin.json` copied to the output; project version equals the manifest version | MSBuild evaluation (`dotnet msbuild -getProperty/-getItem`) |
| after a build: entry type exists, is public and concrete, implements `IPlugin`, has a public parameterless constructor; Contracts reference matches the plugin API | assembly **metadata** (`System.Reflection.Metadata`); nothing is loaded |
| runtime dependencies listed in `deps.json` exist in the output | deps.json |
| declared shared contracts exist, carry the declared assembly name, reference only the framework, `ModularRuntime.Contracts`, and other declared contracts, and are referenced by the entry assembly | `SharedContractRules` (the loader's rules) |
| file names are packageable, no case-only collisions, within package limits | Packaging path rules (`PackagePaths`) |

| Code | Severity | Meaning |
|---|---|---|
| `manifest_missing` | error | no `plugin.json` next to the project |
| `manifest_invalid` | error | the Loader rejected a manifest field (its own code is shown, e.g. `[api_version_incompatible]`, `[shared_contract_missing]`) |
| `entry_assembly_name_mismatch` | error | `entryAssembly` is not what the project builds |
| `target_framework_unsupported` | error | not `net10.0`, or multi-targeting |
| `contracts_reference_missing` | error | no ModularRuntime.Sdk/Contracts reference |
| `host_assembly_referenced` | error | references Engine, Loader, Daemon, Ipc, Packaging, Registry, Desktop, Cli, Publisher, or DevTools, or packages the shared Contracts assembly |
| `entry_assembly_missing` / `entry_assembly_invalid` | error | the built assembly is missing or unreadable |
| `entry_type_not_found` / `entry_type_not_a_plugin` / `entry_type_not_instantiable` | error | the entry type problem found from metadata |
| `api_version_mismatch` | error | compiled against Contracts of another plugin API |
| `dependency_missing` | error | a runtime dependency in deps.json is not in the output |
| `shared_contract_dependency` | error | a shared contract file has another assembly name, is not a valid assembly, or references a private assembly (the runtime would refuse to load it) |
| `unsafe_path` / `path_collision` / `package_limits` | error | the files cannot be packaged |
| `dynamic_loading_disabled` / `manifest_not_copied` / `version_mismatch` / `shared_contract_unused` | warning | likely mistakes |
| `not_built` | info | no build output yet (use `--build`) |

### pack

1. Evaluates and validates the project; if it is invalid nothing is built or written.
2. Builds with `dotnet build -c Release` (`--no-build` uses the existing output).
3. Stages the build output. Excluded: `ModularRuntime.Contracts.*` (shared with the host),
   `.pdb` (unless `--include-symbols`), XML documentation next to assemblies,
   `*.runtimeconfig.dev.json`, OS metadata files, and the reserved names `integrity.json` /
   `integrity.sig`. The project's `plugin.json` is always the one packaged. Private dependencies
   (`ModularRuntime.Sdk.dll`, NuGet packages, shared contract assemblies, native `runtimes/`
   assets) are included.
4. Validates the staged directory exactly as it will be installed.
5. Writes `integrity.json` (path, size, SHA-256 of every file) with the Packaging library the
   daemon uses, and the ZIP with a fixed entry order and timestamps: the same build output always
   produces the same bytes.
6. Prints the package path, file count, size, SHA-256, and the trust notice.

Default output: `bin\<Configuration>\modularruntime\<id>-<version>.zip`, replaced atomically.
Ctrl+C stops the build and leaves no partial package.

```text
<id>-<version>.zip
├── plugin.json
├── <Entry>.dll, <Entry>.deps.json, <Entry>.runtimeconfig.json
├── private dependencies (ModularRuntime.Sdk.dll, shared contracts, NuGet packages, runtimes/…)
└── integrity.json          (and integrity.sig only for official packages)
```

The package has **no** `integrity.sig`: the daemon installs it as *Local, unverified* only after
explicit acceptance. The hashes prove that the installed files are the packaged ones (the daemon
re-checks them before every load), not who made the package or that anyone reviewed it. The tool
has no keys and never creates a signature.

### inspect

Runs the daemon's own `PackageVerifier` on a copy in a temporary directory: archive structure and
limits, signature (when present) with the trusted keys, integrity manifest against the archive
entries, per-file size and SHA-256, `plugin.json`, API compatibility; then reads the entry
assembly's metadata. A signature by an unknown key is rejected and never treated as "unsigned".
`--test-trust-store` adds test keys (development registries only).

### prepare-submission and registry-check

`prepare-submission` creates the folder the administrator reviews
([PUBLISHING.md](PUBLISHING.md#prepare-a-submission)); `registry-check` verifies a catalog revision
with the public publication rules ([OFFICIAL_REGISTRY.md](OFFICIAL_REGISTRY.md)). `doctor` checks
the .NET SDK, the tool, and the project.

## Embedding the runtime

Hosts reference `ModularRuntime.Loader` (which brings `Engine` and `Contracts`) and compose
`PluginRuntime`:

```csharp
await using var runtime = new PluginRuntime(new PluginRuntimeOptions { PluginDirectory = pluginDirectory, Logger = logger });
var discovery = await runtime.DiscoverAsync();
runtime.Host.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition("example.document.exporters", "Exporters", new Version(1, 0)));
await runtime.StartAllAsync();
var exports = await runtime.Host.Extensions.InvokeAllAsync("example.document.exporters", "export", JsonPayload.Parse("""{"title":"T"}"""));
```

The host participates through its own scope (`runtime.Host`): events, JSON/binary/typed services,
streams, extension points. A typed contract the host itself implements must live in a shared
contract assembly declared by the plugins that call it; a copy compiled into the host application
has another identity. Independent `PluginRuntime` instances share nothing. `samples/EmbeddedExample`
and scenario D/G of `tests/ModularRuntime.Template.Tests` show the whole flow.
