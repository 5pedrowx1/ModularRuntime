# ModularRuntime

ModularRuntime is a small extensibility platform for .NET 10. It discovers
plugin installations from a directory, validates a strict manifest, loads each
plugin into its own collectible `AssemblyLoadContext`, and manages a formal
lifecycle with isolated failures. Plugins and the hosting application
communicate through a shared engine:

- an **event bus** (asynchronous publish/subscribe with JSON payloads);
- a **service registry** (request/response services provided by plugins or the host);
- **extension points** (declared by hosts or plugins, filled by contributions);
- **resource ownership** (everything an instance creates is released automatically
  when it stops or fails).

Since v0.3 the runtime runs as an **independent background process** with two
clients:

| Executable | Role |
| --- | --- |
| `ModularRuntime.Daemon.exe` | Sole owner of the engine, plugins, event bus, services, and extensions. No window, one instance per user profile, serves a local named pipe. |
| `ModularRuntime.Cli.exe` | Command-line client: status, daemon start/stop, plugin lifecycle, catalogs, logs. |
| `ModularRuntime.Desktop.exe` | WPF desktop client: dashboard, plugins, events, services, extensions, live logs, settings. |

**Closing the desktop app or the CLI never stops the daemon or its plugins.**
The daemon stops only on an explicit, confirmed request (`daemon stop`, or
*Stop daemon* in the app) or when the Windows session ends. The
`EmbeddedExample` sample still composes `PluginRuntime` in-process.

Since v0.4 the daemon is also a **secure plugin manager**: it installs official plugins from
an authenticated registry (signed catalog, signed packages, SHA-256 of every file) and local
ZIP packages (clearly marked unverified), re-verifies every managed installation before
loading it, and updates or removes plugins atomically. The desktop app (Store: catalog, updates,
Install from ZIP; Plugins: installed plugins) and the CLI are clients of these daemon operations.

Since v0.5 there is a **developer SDK** for plugin authors: `dotnet new modularruntime-plugin`
templates, the `ModularRuntime.Sdk` helpers, and the `dotnet modularruntime` tool
(`validate`, `pack`, `inspect`, `prepare-submission`), all distributed as local NuGet packages.
Start with [GETTING_STARTED.md](docs/GETTING_STARTED.md).

Since v0.6, official plugins are reviewed, rebuilt in a controlled environment, approved, and
signed offline by the ModularRuntime administrator, with separate package and registry keys;
withdrawals and revocations are signed catalog changes enforced by the daemon and shown in the
Store. The administrator's tools live in a separate private repository; everything needed to
develop, submit, install, and **verify** plugins and the official catalog is here
([PLUGIN_SUBMISSION_GUIDE.md](docs/PLUGIN_SUBMISSION_GUIDE.md), [OFFICIAL_REGISTRY.md](docs/OFFICIAL_REGISTRY.md)).

**Status:** v0.6.0 (Official Publishing Infrastructure), pre-release. Implemented, built, and
tested on Windows with .NET SDK 10.0.403. The plugin API is experimental, version `0.3`
([VERSIONING.md](docs/VERSIONING.md)). See [V0.6_OFFICIAL_PUBLISHING.md](docs/V0.6_OFFICIAL_PUBLISHING.md)
for this release. No production key, official catalog URL, or registry repository exists yet. **Signatures establish origin and integrity, not safety: plugins run in-process
without a sandbox** ([PLUGIN_SECURITY_MODEL.md](docs/PLUGIN_SECURITY_MODEL.md)).

## Requirements

- Windows 10/11 (the daemon, the IPC security model, and the desktop app are
  Windows-only);
- .NET SDK 10.0.403 or a compatible 10.0 feature band (`global.json` uses
  `rollForward: latestFeature`); published apps need the .NET 10 Desktop Runtime
  unless published self-contained;
- PowerShell for the scripts.

The runtime has no third-party package dependencies. The test projects use xUnit.

## Quick start

From the repository root:

```powershell
dotnet build ModularRuntime.sln
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/prepare-plugins.ps1 -Configuration Debug
```

The build places the three executables side by side in `artifacts/app/Debug/`.
The default profile is `%LOCALAPPDATA%\ModularRuntime`, with plugins in its
`plugins` folder. Copy staged plugins there (each `plugins/<id>/` folder), or use
a separate profile for experiments:

```powershell
$app = "./artifacts/app/Debug"
& "$app/ModularRuntime.Cli.exe" --profile ./my-profile daemon start
& "$app/ModularRuntime.Cli.exe" --profile ./my-profile plugins list
& "$app/ModularRuntime.Cli.exe" --profile ./my-profile plugins start-all
& "$app/ModularRuntime.Cli.exe" --profile ./my-profile plugins restart com.example.hello
& "$app/ModularRuntime.Cli.exe" --profile ./my-profile logs --follow
& "$app/ModularRuntime.Desktop.exe" --profile ./my-profile
```

Profile layout (created on first use):

```text
<profile>/
  config/daemon.json     optional daemon configuration
  logs/daemon-*.log      daily daemon log files
  plugins/<id>/          plugin installations
  runtime/daemon.json    information about the running daemon (pid, versions)
  desktop/settings.json  desktop preferences
```

The profile comes from `--profile`, then `MODULARRUNTIME_PROFILE`, then
`%LOCALAPPDATA%\ModularRuntime`. Each profile has its own daemon, pipe, and
single-instance lock.

### CLI

```text
status                          Daemon state, version, uptime, and counts
daemon start [--auto-start]     Start the daemon if it is not running
daemon stop                     Stop the daemon gracefully (stops all plugins)
plugins list | info <id> | start <id> | stop <id> | start-all | stop-all
plugins packages | updates | verify <id>
plugins install <id> [--version <v>]
plugins install-zip <file.zip> [--allow-unverified] [--stop-if-running]
plugins update <id> [--stop-if-running]
plugins remove <id> [--stop-if-running] [--yes]
registry status | refresh | list | info <id>
services list
extensions list
events subscriptions
logs [--follow] [--level <level>] [--plugin <id>] [--category <name>] [--search <text>] [--limit <n>]

Global options: --profile <dir>, --json, --timeout <sec>, --help
Exit codes: 0 success, 1 operation failed, 2 usage, 3 daemon unavailable,
            4 request failed, 130 canceled
```

### Desktop

`ModularRuntime.Desktop.exe` connects to the profile's daemon and starts it if it
is not running (configurable in Settings; `--no-autostart` disables it for one
launch). The connection indicator shows Connected, Connecting, Starting,
Stopping, Disconnected, or Error. When the daemon goes away the app keeps
running, shows Disconnected, reconnects with bounded backoff, and offers *Retry*
and *Start daemon*.

### Publish

```powershell
./scripts/publish.ps1                 # artifacts/publish/win-x64, framework-dependent
./scripts/publish.ps1 -SelfContained  # bundles the .NET runtime
```

### Embedded example

```powershell
dotnet run --project ./samples/EmbeddedExample -- --plugins ./plugins
```

It declares a host extension point and a host service, discovers and starts all
plugins in manifest start order, publishes an event, invokes a plugin service,
invokes the plugin contributions to its extension point, shows that the failing
cleanup demo left no resources behind, then stops everything and disposes the
runtime.

## Embedded usage

`PluginRuntime` is the public composition point for applications that want the
loader and engine in-process:

```csharp
using ModularRuntime.Contracts;
using ModularRuntime.Loader;

await using var runtime = new PluginRuntime(new PluginRuntimeOptions
{
    PluginDirectory = pluginDirectory,
    Logger = logger, // IRuntimeLogger; defaults to a no-op logger
});

var discovery = await runtime.DiscoverAsync();
foreach (var failure in discovery.RegistrationFailures)
{
    Console.WriteLine($"{failure.PluginId}: {failure.Message}");
}

// The host participates in the platform through its own scope.
runtime.Host.Extensions.DeclareExtensionPoint(
    new ExtensionPointDefinition("example.document.exporters", "Exporters", new Version(1, 0)));

await runtime.StartAllAsync();

var published = await runtime.Host.Events.PublishAsync(
    "example.greetings", JsonPayload.Parse("""{"text":"hello"}"""));
var reversed = await runtime.Host.Services.InvokeAsync(
    "example.text.transform", "reverse", JsonPayload.Parse("""{"text":"abc"}"""));
var exports = await runtime.Host.Extensions.InvokeAllAsync(
    "example.document.exporters", "export", JsonPayload.Parse("""{"title":"T","body":"B"}"""));
```

Independent `PluginRuntime` instances share nothing: events, services,
extension points, and identities are scoped to one runtime.

The daemon has no special loading or lifecycle logic either: it is an IPC front
end over the same `PluginRuntime` facade.

## Repository layout

```text
src/
  ModularRuntime.Contracts/     Public SDK: lifecycle, context, events, services,
                                extensions, ownership, JSON payloads, results
  ModularRuntime.Engine/        Lifecycle, event bus, service and extension
                                registries, resource ownership, concurrency
  ModularRuntime.Loader/        Discovery, manifest validation, collectible ALCs
  ModularRuntime.Ipc.Contracts/ IPC protocol v1: messages, operations, DTOs
  ModularRuntime.Ipc/           Framing, pipe security, server, client, profiles,
                                daemon launcher
  ModularRuntime.Daemon/        Background process that owns the runtime
  ModularRuntime.Cli/           Command-line IPC client
  ModularRuntime.Desktop/       WPF IPC client (MVVM)
  ModularRuntime.Packaging/     Package format, verification, trust store, atomic install store
  ModularRuntime.Registry/      Official registry: authenticated catalog, rollback, downloads
  ModularRuntime.Sdk/           Helpers for plugin authors (NuGet; private to each plugin)
  ModularRuntime.Templates/     dotnet new template package (modularruntime-plugin)
  ModularRuntime.Submissions/   Public submission format and secret classifier
  ModularRuntime.DevTools/      dotnet modularruntime: validate, pack, inspect, prepare-submission,
                                registry-check
samples/
  HelloPlugin/                  Minimal working plugin
  FailingPlugin/                Plugin that fails during initialization
  EventPublisherPlugin/         Publishes a JSON event
  EventSubscriberPlugin/        Subscribes and logs events
  ServiceProviderPlugin/        Provides example.text.transform
  ServiceConsumerPlugin/        Discovers and invokes the service
  ExtensionContributorPlugin/   Contributes to the embedded host's extension point
  FailureCleanupPlugin/         Creates resources, fails, and is cleaned up
  EmbeddedExample/              In-process host sample
tests/
  ModularRuntime.Engine.Tests/
  ModularRuntime.Loader.Tests/
  ModularRuntime.Integration.Tests/
  ModularRuntime.Ipc.Tests/     Framing, handshake, security, concurrency
  ModularRuntime.Daemon.Tests/  Log store, options, real-process independence tests
  ModularRuntime.Desktop.Tests/ View models and connection supervisor
  ModularRuntime.Packaging.Tests/ Malicious ZIPs, signatures, installation with real ALCs,
                                repository hygiene (no keys, no administrative code)
  ModularRuntime.Registry.Tests/  Catalog authenticity, rollback, expiry, downloads, publication rules
  ModularRuntime.Sdk.Tests/     SDK helpers against the real engine
  ModularRuntime.DevTools.Tests/ validate, pack, inspect, submission with real builds
  ModularRuntime.Template.Tests/ Packed SDK used outside the repository: templates, tool,
                                daemon, embedded host (scenarios A-H)
  ModularRuntime.TestSupport/   Shared test infrastructure
  fixtures/                     Plugin fixtures and a private dependency
plugins/                        Generated staging root; only README is tracked
scripts/prepare-plugins.ps1     Builds and stages the sample plugins
scripts/publish.ps1             Publishes the three executables into one folder
scripts/pack-sdk.ps1            Packs the SDK NuGet packages into artifacts/nuget
docs/                           Architecture and operational documentation
```

## Plugin model

A plugin is a class library that references `ModularRuntime.Contracts`,
implements `IPlugin`, and ships a `plugin.json` manifest next to its entry
assembly. It must have a public parameterless constructor and be loaded from a
path contained by its installation directory.

Minimal manifest:

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

`startOrder` is optional; bulk start runs in ascending order and bulk stop in
reverse.

The quickest start is the template and the developer tool:

```powershell
./scripts/pack-sdk.ps1                     # local NuGet packages in artifacts/nuget
dotnet new install ./artifacts/nuget/ModularRuntime.Templates.0.6.0.nupkg
dotnet tool install --global ModularRuntime.DevTools --version 0.6.0 --add-source ./artifacts/nuget
dotnet new modularruntime-plugin -n MyFirstPlugin
cd MyFirstPlugin; dotnet modularruntime validate --build; dotnet modularruntime pack
```

See [GETTING_STARTED.md](docs/GETTING_STARTED.md) for the whole flow, and
[PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md) for the full schema,
build instructions, dependencies, cancellation rules, and error codes.

## Build and test

```powershell
dotnet build ModularRuntime.sln -c Debug
dotnet build ModularRuntime.sln -c Release

dotnet test ModularRuntime.sln -c Debug --no-build
dotnet test ModularRuntime.sln -c Release --no-build
```

Warnings are treated as errors. Test projects stage plugin assets into their
output directory during the build and copy them into isolated temporary
directories at test time.

The same restore, build, and test run in GitHub Actions on Windows for both
configurations ([.github/workflows/ci.yml](.github/workflows/ci.yml)), with a
read-only token, no secrets, and actions pinned by commit SHA. CI never signs,
packs for publication, or publishes anything.

`ModularRuntime.Daemon.Tests` starts the real executables from
`artifacts/app/<Configuration>/`, each test with its own temporary profile
(unique pipe and lock); it never touches the default profile. Three of those
tests start the WPF app and need an interactive desktop session. A running
daemon locks the files in `artifacts/app/<Configuration>/`, so stop it
(`daemon stop`) before rebuilding that configuration.

This repository builds and tests on its own; it references no other repository.

## Documentation

- [PLUGIN_SUBMISSION_POLICY.md](docs/PLUGIN_SUBMISSION_POLICY.md) — how official plugins are reviewed,
  built, approved, signed, and published.
- [PLUGIN_SIGNING.md](docs/PLUGIN_SIGNING.md), [OFFICIAL_REGISTRY.md](docs/OFFICIAL_REGISTRY.md),
  [PACKAGE_REVOCATION.md](docs/PACKAGE_REVOCATION.md) — signatures, keys, the signed catalog,
  withdrawal and revocation, and how to verify them (`dotnet modularruntime inspect`, `registry-check`).
- [V0.6_OFFICIAL_PUBLISHING.md](docs/V0.6_OFFICIAL_PUBLISHING.md) — v0.6 overview and limitations.
- [SDK_OVERVIEW.md](docs/SDK_OVERVIEW.md) — the developer SDK: packages, templates, tool.
- [GETTING_STARTED.md](docs/GETTING_STARTED.md) — first plugin from `dotnet new` to running.
- [PLUGIN_API.md](docs/PLUGIN_API.md), [EVENT_BUS_GUIDE.md](docs/EVENT_BUS_GUIDE.md),
  [SERVICE_REGISTRY_GUIDE.md](docs/SERVICE_REGISTRY_GUIDE.md),
  [EXTENSION_POINTS_GUIDE.md](docs/EXTENSION_POINTS_GUIDE.md) — the plugin API.
- [PLUGIN_PACKAGING.md](docs/PLUGIN_PACKAGING.md) — `dotnet modularruntime` validate, pack,
  inspect; diagnostics.
- [PLUGIN_SUBMISSION_GUIDE.md](docs/PLUGIN_SUBMISSION_GUIDE.md) — preparing a submission.
- [VERSIONING.md](docs/VERSIONING.md) — product version and plugin API version.
- [V0.5_DEVELOPER_SDK.md](docs/V0.5_DEVELOPER_SDK.md) — v0.5 overview, tests, and limitations.
- [V0.4_PLUGIN_MANAGER.md](docs/V0.4_PLUGIN_MANAGER.md) — v0.4 overview: plugin manager,
  registry, GUI, CLI, tests, and limitations.
- [PLUGIN_SECURITY_MODEL.md](docs/PLUGIN_SECURITY_MODEL.md) — what signatures and hashes mean,
  trust states, verification before load.
- [PLUGIN_PACKAGE_FORMAT.md](docs/PLUGIN_PACKAGE_FORMAT.md) — package ZIP, `integrity.json`,
  path rules, limits, error codes.
- [PLUGIN_SIGNING.md](docs/PLUGIN_SIGNING.md) — algorithm, trust store, keys, publisher commands.
- [OFFICIAL_REGISTRY.md](docs/OFFICIAL_REGISTRY.md) — catalog format, authenticity, rollback,
  offline use.
- [PLUGIN_SUBMISSION_POLICY.md](docs/PLUGIN_SUBMISSION_POLICY.md) — submissions, review,
  controlled builds, approval.
- [V0.3_DESKTOP.md](docs/V0.3_DESKTOP.md) — v0.3 overview: processes, desktop app,
  CLI, tests, and limitations.
- [IPC_PROTOCOL.md](docs/IPC_PROTOCOL.md) — framing, handshake, operations,
  notifications, errors, and limits.
- [PROCESS_LIFECYCLE.md](docs/PROCESS_LIFECYCLE.md) — daemon startup, single
  instance, shutdown, and client independence.
- [ARCHITECTURE.md](docs/ARCHITECTURE.md) — component boundaries, loading model,
  concurrency, and design decisions.
- [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md) — creating and packaging a
  plugin.
- [LIFECYCLE.md](docs/LIFECYCLE.md) — plugin states, transitions, cancellation,
  and shutdown behavior.
- [SECURITY.md](docs/SECURITY.md) — trust model, IPC security, and limitations.
- [ROADMAP.md](docs/ROADMAP.md) — shipped scope and planned work.
- [V0.2_EXTENSIBILITY.md](docs/V0.2_EXTENSIBILITY.md) — event bus, services,
  extension points, ownership, memory model, API changes, and limitations.
- [V0.2.1_CONCURRENCY.md](docs/V0.2.1_CONCURRENCY.md) — v0.2.1 audit and fixes for
  concurrent resource release and scope closing.
- [V0.1.1_STABILITY.md](docs/V0.1.1_STABILITY.md) — v0.1.1 concurrency,
  memory-retention, and failure-handling fixes.
- [STORE_AND_RESTART.md](docs/STORE_AND_RESTART.md) — desktop Store and Plugins pages,
  and start/stop/start and restart of plugins in the same daemon.

## Known limitations

- Plugins are trusted code. `AssemblyLoadContext` provides dependency and
  identity isolation, **not** a security sandbox. The event bus, service
  registry, and extension points do not enforce permissions either.
- The IPC trust boundary is the Windows user: any process running as the same
  user can control that user's daemon. Remote connections are rejected.
- The daemon is a per-user background process, not a Windows Service, and it
  does not start with Windows.
- The CLI and the desktop app do not publish events or invoke services (the old
  interactive host could); IPC exposes only validated administrative operations.
- The plugin API is experimental (`0.3`); every 0.x minor may break plugins.
- The SDK packages are local only (not on nuget.org) while ModularRuntime is pre-release; no
  license or repository URL is declared in their metadata yet.
- Restarting a plugin creates a new load context each time; a plugin that keeps
  references to its own context delays collection of the old one (see
  [STORE_AND_RESTART.md](docs/STORE_AND_RESTART.md)).
- No production signing key and no default registry location exist yet (pre-release);
  development uses test keys passed explicitly with `--test-trust-store`.
- Collection of an unloaded plugin context is cooperative and depends on the
  plugin releasing its own references.
- No automatic updates, online key revocation, dependency resolution between plugins,
  health checks, hot reload, or remote management API.
- Code that ignores cancellation cannot be interrupted in-process. Daemon
  shutdown is bounded by `--shutdown-timeout`; past it the daemon exits with code
  3 and the plugin's remaining work ends with the process.
- Event dispatch is sequential per publication; a slow subscriber delays the
  publisher.
- Only Windows has been validated.

## License

ModularRuntime is licensed under the [Apache License, Version 2.0](LICENSE); see also [NOTICE](NOTICE).
The NuGet packages declare `Apache-2.0`. Plugins are separate works: each plugin keeps the license its
author chooses (declared in its submission and shown in the Store).
