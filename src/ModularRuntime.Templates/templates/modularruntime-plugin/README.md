# Modular Runtime Plugin Display Name

A ModularRuntime plugin (`example.plugin.id`, plugin API @@PLUGIN_API@@).

## Build, validate, pack

```
dotnet build
dotnet modularruntime validate --build
dotnet modularruntime pack
```

`pack` writes `bin/Release/modularruntime/example.plugin.id-1.0.0.zip`: a **local, unsigned**
package. Install it from the desktop app (Store → Install from ZIP) or with
`modularruntime plugins install-zip <file.zip> --allow-unverified`. It is shown as
"Local, unverified": only packages built and signed by the ModularRuntime team are official.

The developer tool is a .NET tool: `dotnet tool install --global ModularRuntime.DevTools`.

## Files

- `ModularRuntimePlugin.csproj` — targets net10.0, references `ModularRuntime.Sdk`.
- `ModularRuntimePlugin.cs` — the entry type (`IPlugin`).
- `plugin.json` — the manifest: `id`, `name`, `version` (keep it equal to the project
  `<Version>`), `apiVersion`, `entryAssembly`, `entryType`, `author`, `description`.

## Lifecycle

The runtime creates the entry type with its public parameterless constructor, calls
`InitializeAsync(context, cancellationToken)` when the plugin starts and
`ShutdownAsync(cancellationToken)` when it stops. Every start (also after Stop or Restart) uses
a new instance in a new load context. Everything registered through `context` (event
subscriptions, services, contributions, tracked resources) is removed automatically when the
plugin stops.

## Communication

Plugins talk through the runtime, never through direct references: JSON events and services,
typed services (a C# interface from a shared contract assembly declared in `plugin.json` as
`sharedContracts`), binary services (bytes with explicit buffer ownership), and streams (buffered
delivery with backpressure or loss). `dotnet new modularruntime-plugin --kind …` has a working
example of each.

See the ModularRuntime documentation: GETTING_STARTED, PLUGIN_API, COMMUNICATION,
PLUGIN_PACKAGING, PLUGIN_SUBMISSION_GUIDE.
