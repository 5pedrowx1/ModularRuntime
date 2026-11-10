# ModularRuntime.Sdk

Helpers for ModularRuntime plugin authors, built on `ModularRuntime.Contracts` (plugin API 0.3).

- `PluginJson` — convert your own types to and from `JsonPayload`, the neutral JSON
  representation that crosses every boundary between plugins.
- `PluginEventBusExtensions` — `PublishAsync<T>` and `Subscribe<T>` on `context.Events`.
- `PluginOperations` — route service or contribution operations to typed handlers;
  `context.Services.Register(id, name, version, operations)` and
  `context.Extensions.Contribute(definition, operations)`.
- `InvokeAsync<TRequest, TResponse>` on `context.Services` and `context.Extensions`, returning
  `PluginCallResult<T>` (a missing or stopped service is a failed result, not an exception).
- `context.RunInBackground(name, work)` — background work that stops with the plugin.

Everything registered through the context is owned by the plugin instance and removed when it
stops. This assembly is private to each plugin (copied into the plugin package);
`ModularRuntime.Contracts` is shared with the host and is not packaged with plugins.

Create a plugin with the template: `dotnet new install ModularRuntime.Templates`, then
`dotnet new modularruntime-plugin -n MyFirstPlugin`.
