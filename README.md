# ModularRuntime

ModularRuntime is a small extensibility platform for .NET 10. It discovers plugin installations
from a directory, validates a strict manifest, loads each plugin into its own collectible
`AssemblyLoadContext`, manages a formal lifecycle with isolated failures, and gives plugins and the
hosting application a **communication platform**:

- an **event bus** (asynchronous publish/subscribe with JSON payloads);
- **JSON services** (request/response by ID and operation, without shared types);
- **typed services** (a C# interface from a shared contract assembly, called through a
  runtime-generated proxy: direct calls, no serialization);
- **binary services** (bytes in, bytes out, with explicit buffer ownership);
- **streams** (one producer, many subscribers, bounded buffers with backpressure or controlled loss);
- **extension points** (declared by hosts or plugins, filled by contributions);
- **resource ownership** (everything an instance creates is released automatically when it stops
  or fails).

The runtime runs as an **independent background process** with two clients:

| Executable | Role |
|---|---|
| `ModularRuntime.Daemon.exe` | Sole owner of the engine, plugins, and the communication platform; secure plugin manager (signed packages, authenticated registry). No window, one instance per user profile, serves a local named pipe. |
| `ModularRuntime.Cli.exe` | Command-line client: status, daemon start/stop, plugin lifecycle, packages, catalogs (services, streams, extensions, events), logs. |
| `ModularRuntime.Desktop.exe` | WPF desktop client: dashboard, Store, plugins, events, services, extensions, streams, live logs, settings. |

**Closing the desktop app or the CLI never stops the daemon or its plugins.** The
`EmbeddedExample` sample composes the same `PluginRuntime` in-process.

Plugin authors get a **developer SDK**: `dotnet new modularruntime-plugin` templates (eleven
kinds), the `ModularRuntime.Sdk` helpers, and the `dotnet modularruntime` tool (`validate`, `pack`,
`inspect`, `prepare-submission`, `registry-check`). Official plugins are reviewed, rebuilt in a
controlled environment, approved, and signed offline by the ModularRuntime administrator;
everything needed to develop, submit, install, and **verify** plugins and the official catalog is
in this repository.

**Status:** 0.7.0 (communication platform), pre-release. Implemented, built, and tested on Windows
with .NET SDK 10.0.403. The plugin API is experimental, version `0.4`
([VERSIONING.md](docs/VERSIONING.md)); what changed per version is in
[CHANGELOG.md](docs/CHANGELOG.md). No production key, official catalog URL, or registry repository
exists yet. **Signatures establish origin and integrity, not safety: plugins run in-process without
a sandbox** ([SECURITY.md](docs/SECURITY.md)).

## Requirements

- Windows 10/11 (the daemon, the IPC security model, and the desktop app are Windows-only);
- .NET SDK 10.0.403 or a compatible 10.0 feature band (`global.json` uses `rollForward:
  latestFeature`); published apps need the .NET 10 Desktop Runtime unless published self-contained;
- PowerShell for the scripts.

The runtime has no third-party package dependencies. The tests use xUnit; the benchmarks use
BenchmarkDotNet.

## Quick start

```powershell
dotnet build ModularRuntime.sln
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/prepare-plugins.ps1 -Configuration Debug
```

The build places the three executables side by side in `artifacts/app/Debug/`. The default
profile is `%LOCALAPPDATA%\ModularRuntime`, with plugins in its `plugins` folder; use a separate
profile for experiments:

```powershell
$app = "./artifacts/app/Debug"
& "$app/ModularRuntime.Cli.exe" --profile ./my-profile daemon start
& "$app/ModularRuntime.Cli.exe" --profile ./my-profile plugins list
& "$app/ModularRuntime.Cli.exe" --profile ./my-profile plugins start-all
& "$app/ModularRuntime.Cli.exe" --profile ./my-profile services list
& "$app/ModularRuntime.Cli.exe" --profile ./my-profile streams list
& "$app/ModularRuntime.Cli.exe" --profile ./my-profile logs --follow
& "$app/ModularRuntime.Desktop.exe" --profile ./my-profile
```

The staged samples (`plugins/<id>/`) include the typed telemetry provider and consumer, the binary
checksum service, and the stream publisher and subscriber; copy them into the profile's `plugins`
folder or point the daemon at `./plugins`. The embedded host:

```powershell
dotnet run --project ./samples/EmbeddedExample -- --plugins ./plugins
```

Your first plugin, from the packed SDK:

```powershell
./scripts/pack-sdk.ps1                     # local NuGet packages in artifacts/nuget (0.7.0)
dotnet new install ./artifacts/nuget/ModularRuntime.Templates.0.7.0.nupkg
dotnet tool install --global ModularRuntime.DevTools --version 0.7.0 --add-source ./artifacts/nuget
dotnet new modularruntime-plugin -n MyFirstPlugin --kind typed-provider
cd MyFirstPlugin; dotnet modularruntime validate --build; dotnet modularruntime pack
```

See [GETTING_STARTED.md](docs/GETTING_STARTED.md).

## Embedded usage

```csharp
using ModularRuntime.Contracts;
using ModularRuntime.Loader;

await using var runtime = new PluginRuntime(new PluginRuntimeOptions { PluginDirectory = pluginDirectory, Logger = logger });
var discovery = await runtime.DiscoverAsync();
runtime.Host.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition("example.document.exporters", "Exporters", new Version(1, 0)));
await runtime.StartAllAsync();

var published = await runtime.Host.Events.PublishAsync("example.greetings", JsonPayload.Parse("""{"text":"hello"}"""));
var reversed = await runtime.Host.Services.InvokeAsync("example.text.transform", "reverse", JsonPayload.Parse("""{"text":"abc"}"""));
using var crc = await runtime.Host.Services.InvokeBinaryAsync("example.checksum", "crc32", "hello"u8.ToArray());
var ticks = runtime.Host.Streams.Subscribe<JsonPayload>("example.ticks");
```

Independent `PluginRuntime` instances share nothing. The daemon is an IPC front end over the same
facade.

## Repository layout

```text
src/
  ModularRuntime.Contracts/     Public plugin API: lifecycle, context, events, JSON/binary/typed services,
                                streams, extension points, ownership, JSON payloads, results
  ModularRuntime.Engine/        Lifecycle, event bus, service registry (with typed proxies), stream
                                registry, extension registry, resource ownership, concurrency
  ModularRuntime.Loader/        Discovery, manifest validation, collectible ALCs, shared contract contexts
  ModularRuntime.Ipc.Contracts/ IPC protocol v1: messages, operations, DTOs
  ModularRuntime.Ipc/           Framing, pipe security, server, client, profiles, daemon launcher
  ModularRuntime.Daemon/        Background process that owns the runtime and the plugin manager
  ModularRuntime.Cli/           Command-line IPC client
  ModularRuntime.Desktop/       WPF IPC client (MVVM)
  ModularRuntime.Packaging/     Package format, verification, trust store, atomic install store
  ModularRuntime.Registry/      Official registry: authenticated catalog, rollback, publication rules
  ModularRuntime.Sdk/           Helpers for plugin authors (NuGet; private to each plugin)
  ModularRuntime.Templates/     dotnet new template package (modularruntime-plugin, 11 kinds)
  ModularRuntime.Submissions/   Public submission format and secret classifier
  ModularRuntime.DevTools/      dotnet modularruntime: validate, pack, inspect, prepare-submission, registry-check
samples/
  HelloPlugin, FailingPlugin, EventPublisherPlugin, EventSubscriberPlugin, ServiceProviderPlugin,
  ServiceConsumerPlugin, ExtensionContributorPlugin, FailureCleanupPlugin
  Example.Telemetry.Contracts/  A shared contract assembly (typed service and stream contracts)
  TypedProviderPlugin/          Provides the typed service example.telemetry and a typed stream
  TypedConsumerPlugin/          Calls it through the proxy, reads the stream, calls the binary service
  BinaryProviderPlugin/         Provides the binary service example.checksum
  StreamPublisherPlugin/        Writes a JSON stream and a binary stream
  StreamSubscriberPlugin/       Reads them with backpressure and with a drop policy
  EmbeddedExample/              In-process host sample
benchmarks/ModularRuntime.Benchmarks/   BenchmarkDotNet suite (not part of the tests)
tests/                          Engine, Loader, Integration, Ipc, Daemon, Desktop, Packaging, Registry,
                                Sdk, DevTools, Template tests, TestSupport, fixtures
plugins/                        Generated staging root; only README is tracked
scripts/                        prepare-plugins, publish, pack-sdk
docs/                           Documentation (below)
```

## Plugin model

A plugin is a class library that references `ModularRuntime.Contracts` (through
`ModularRuntime.Sdk`), implements `IPlugin`, and ships a `plugin.json` next to its entry assembly:

```json
{
  "id": "com.example.typed.provider",
  "name": "Typed Service Provider Plugin",
  "version": "1.0.0",
  "apiVersion": "0.4",
  "entryAssembly": "TypedProviderPlugin.dll",
  "entryType": "TypedProviderPlugin.TypedProviderPlugin",
  "startOrder": 10,
  "sharedContracts": ["Example.Telemetry.Contracts"]
}
```

`startOrder` and `sharedContracts` are optional. The full schema, the context API, and the
error codes are in [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md); the communication
mechanisms in [COMMUNICATION.md](docs/COMMUNICATION.md).

## Build and test

```powershell
dotnet build ModularRuntime.sln -c Debug
dotnet build ModularRuntime.sln -c Release
dotnet test ModularRuntime.sln -c Debug --no-build
dotnet test ModularRuntime.sln -c Release --no-build
dotnet run -c Release --project benchmarks/ModularRuntime.Benchmarks -- --filter "*"
```

Warnings are treated as errors. Test projects stage plugin assets into their output directory
during the build and copy them into isolated temporary directories at test time. The same
restore, build, and test run in GitHub Actions on Windows for both configurations
([.github/workflows/ci.yml](.github/workflows/ci.yml)) with a read-only token, no secrets, and
actions pinned by commit SHA; CI never signs, packs for publication, or publishes anything.

`ModularRuntime.Daemon.Tests` and `ModularRuntime.Template.Tests` start the real executables
from `artifacts/app/<Configuration>/`, each test with its own temporary profile; three daemon
tests start the WPF app and need an interactive desktop session. A running daemon locks the files
in `artifacts/app/<Configuration>/`, so stop it before rebuilding that configuration.

## Documentation

| Document | Contents |
|---|---|
| [GETTING_STARTED.md](docs/GETTING_STARTED.md) | First plugin from `dotnet new` to running in the daemon |
| [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md) | The plugin API: project, entry type, context, lifecycle contract, manifest schema, installation layout, error codes |
| [COMMUNICATION.md](docs/COMMUNICATION.md) | Event bus, JSON/typed/binary services, shared contract assemblies, streams, extension points, ownership, error model, concurrency and memory model |
| [SDK.md](docs/SDK.md) | Packages, `ModularRuntime.Sdk` helpers, template kinds, `dotnet modularruntime` reference and diagnostics, embedding |
| [LIFECYCLE.md](docs/LIFECYCLE.md) | Plugin states, start, stop, teardown order, restart, disposal, unload tracking |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Components, engine, loader, daemon, concurrency model, design decisions |
| [OPERATIONS.md](docs/OPERATIONS.md) | Daemon, profiles, CLI, desktop app, plugin manager, restart, verified scenarios, publishing the app |
| [IPC_PROTOCOL.md](docs/IPC_PROTOCOL.md) | Framing, handshake, operations, notifications, errors, limits |
| [SECURITY.md](docs/SECURITY.md) | Trust model, communication platform, IPC security, packages and verification before load, exposure |
| [PLUGIN_PACKAGE_FORMAT.md](docs/PLUGIN_PACKAGE_FORMAT.md) | Package ZIP, `integrity.json`, path rules, limits, verification order, error codes |
| [PLUGIN_SIGNING.md](docs/PLUGIN_SIGNING.md) | Signatures, key purposes, trust store, rotation, key handling |
| [OFFICIAL_REGISTRY.md](docs/OFFICIAL_REGISTRY.md) | Catalog format, authenticity, rollback, offline use, publication rules |
| [PUBLISHING.md](docs/PUBLISHING.md) | Submission, review, controlled build, approval, signing, withdrawal and revocation |
| [VERSIONING.md](docs/VERSIONING.md) | Product version and plugin API version |
| [BENCHMARKS.md](docs/BENCHMARKS.md) | Benchmark methodology and measured results |
| [REPOSITORY_ORGANIZATION.md](docs/REPOSITORY_ORGANIZATION.md) | Projects, what is distributed, what is kept elsewhere |
| [CHANGELOG.md](docs/CHANGELOG.md) | What each version changed |
| [ROADMAP.md](docs/ROADMAP.md) | Candidate work, non-goals, compatibility policy |

## Known limitations

- Plugins are trusted code. `AssemblyLoadContext` provides dependency and identity isolation,
  **not** a sandbox; the communication platform enforces no permissions.
- The IPC trust boundary is the Windows user: any process running as the same user can control
  that user's daemon. Remote connections are rejected.
- The daemon is a per-user background process, not a Windows Service, and does not start with
  Windows. Clients do not publish events, invoke services, or write streams.
- The plugin API is experimental (`0.4`); every 0.x minor may break plugins. The SDK packages are
  local only (not on nuget.org).
- Typed services require a shared contract assembly declared by every plugin that uses it; a
  contract compiled into the host application has another identity. Values crossing typed
  boundaries must be shared all the way down (the runtime refuses the rest by default); callbacks
  cross through `RegisterCallback`, never as raw delegates.
- Collection of an unloaded plugin or contract context is cooperative. Code that ignores
  cancellation cannot be interrupted in-process; daemon shutdown is bounded by `--shutdown-timeout`.
- No production signing key and no default registry location exist yet. No automatic updates,
  online key revocation, declarative dependencies between plugins, health checks, hot reload, or
  remote management API.
- Event dispatch is sequential per publication; a slow subscriber delays the publisher (streams
  decouple them).
- Only Windows has been validated.

## License

ModularRuntime is licensed under the [Apache License, Version 2.0](LICENSE); see also [NOTICE](NOTICE).
The NuGet packages declare `Apache-2.0`. Plugins are separate works: each plugin keeps the license
its author chooses (declared in its submission and shown in the Store).
