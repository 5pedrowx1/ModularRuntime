# Service registry guide

A plugin registers a **service** (an ID, a name, a version, and named operations). Other plugins
and the host call it by ID with JSON. Provider and consumer never reference each other's
assemblies.

Templates: `--kind service-provider --service-id contoso.text` and
`--kind service-consumer --service-id contoso.text`. They are exercised end to end in scenario C
of `tests/ModularRuntime.Template.Tests`.

## Provide a service

```csharp
using ModularRuntime.Sdk;

public sealed record TextRequest(string Text);
public sealed record TextResponse(string Text, int Length);

var operations = new PluginOperations()
    .Add<TextRequest, TextResponse>("upper", (request, call, token) =>
        ValueTask.FromResult(new TextResponse(request.Text.ToUpperInvariant(), request.Text.Length)))
    .Add<TextRequest, TextResponse>("reverse", async (request, call, token) =>
    {
        await Task.Yield();
        if (request.Text.Length == 0)
        {
            throw new PluginOperationException("empty_text", "Text must not be empty.");   // expected failure
        }

        return new TextResponse(new string([.. request.Text.Reverse()]), request.Text.Length);
    });

context.Services.Register("contoso.text", "Text service", new Version(1, 0), operations);
```

`PluginOperations` routes operations to typed handlers and answers the standard failures for you:

| Situation | Response |
|---|---|
| unknown operation | `operation_not_supported` |
| request JSON does not match the request type | `invalid_payload` |
| handler throws `PluginOperationException(code, message)` | that code and message |
| handler throws anything else | `handler_failed` with a diagnostic (reported by the runtime) |

Without the SDK: `context.Services.Register(new PluginServiceDefinition(id, name, version, ["upper"]), handler)`,
where `handler` is a `PluginInvocationHandler` that returns `PluginResponse.Success(payload)` or
`PluginResponse.Failure(code, message)`.

## Call a service

```csharp
var result = await context.Services.InvokeAsync<TextRequest, TextResponse>("contoso.text", "upper", new TextRequest("hello"), cancellationToken: token);
if (result.IsSuccess)
{
    context.Logger.LogInformation(result.Value!.Text);
}
else
{
    context.Logger.LogWarning($"[{result.ErrorCode}] {result.Message}");   // e.g. service_not_found
}
```

- A missing, removed, or stopped service is a **failed result** (`service_not_found`,
  `service_unavailable`, `service_removed`), never an exception and never a call into a
  stopped plugin.
- `ServiceInvocationOptions { MinimumVersion, CorrelationId }` requires a minimum service version
  (`service_version_incompatible`).
- Cancellation: the token you pass reaches the provider's handler; a canceled call returns
  `Status == Canceled`.
- Discovery: `context.Services.GetService(id)` returns the `PluginServiceDescriptor` (version,
  operations, owner, availability), and `GetServices()` lists all of them. `GetClient(id)` returns
  a handle bound to the current registration. The handle never keeps the provider alive and
  reports `IsAvailable == false` after the provider stops.

## Ownership

The registration belongs to the providing instance and is removed when it stops. Consumers then
get `service_not_found`. The consumer template shows this: it keeps running and logs the
failure until a provider is available again.
