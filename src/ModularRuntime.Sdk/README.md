# ModularRuntime.Sdk

Helpers for ModularRuntime plugin authors, built on `ModularRuntime.Contracts` (plugin API 0.4).

- `PluginJson` — convert your own types to and from `JsonPayload`, the neutral JSON
  representation that crosses every boundary between plugins.
- `PluginEventBusExtensions` — `PublishAsync<T>` and `Subscribe<T>` on `context.Events`.
- `PluginOperations` — route service or contribution operations to typed handlers;
  `context.Services.Register(id, name, version, operations)` and
  `context.Extensions.Contribute(definition, operations)`.
- `InvokeAsync<TRequest, TResponse>` on `context.Services` and `context.Extensions`, returning
  `PluginCallResult<T>` (a missing or stopped service is a failed result, not an exception).
- `context.RunInBackground(name, work)` — background work that stops with the plugin.
- `BinaryOperations` — route the operations of a binary service (bytes in, bytes out);
  `context.Services.RegisterBinary(id, name, version, operations)`.
- `context.Consume<T>(streamId, handler)` / `ConsumeJson<T>` — read a stream in the background until
  it completes or the plugin stops; `stream.WriteJsonAsync(item)` for `JsonPayload` streams.

Typed services need no helper: `context.TypedServices.Register<IContract>(definition, instance)` and
`context.TypedServices.Resolve<IContract>(id)` work on the contracts directly, provided the contract
assembly is declared in `plugin.json` (`sharedContracts`) by every plugin that uses it.

Everything registered through the context is owned by the plugin instance and removed when it
stops. This assembly is private to each plugin (copied into the plugin package);
`ModularRuntime.Contracts` is shared with the host and is not packaged with plugins.

Create a plugin with the template: `dotnet new install ModularRuntime.Templates`, then
`dotnet new modularruntime-plugin -n MyFirstPlugin`.
