# Extension points guide

A host (or a plugin) **declares an extension point**: an ID, a contract version, and the
operations a contribution answers. Plugins **contribute** implementations. The declarer lists the
contributions and invokes one or all of them. This is the same registry as before: there is no
second mechanism.

Template: `--kind extension --extension-point contoso.formatters`. It is exercised end to end in
scenario D of `tests/ModularRuntime.Template.Tests`, where a generated host application declares
the point and runs the generated contribution.

## Declare (host or plugin)

```csharp
await using var point = runtime.Host.Extensions.DeclareExtensionPoint(
    new ExtensionPointDefinition("contoso.formatters", "Formatters", new Version(1, 0)) { Operations = ["format"] });
```

A plugin declares through `context.Extensions` instead. The point is then owned by that plugin
and removed when it stops, together with its contributions.

## Contribute (plugin)

```csharp
using ModularRuntime.Sdk;

public sealed record FormatRequest(string Text);
public sealed record FormatResponse(string Text);

if (context.Extensions.GetExtensionPoint("contoso.formatters") is { } point)
{
    context.Extensions.Contribute(
        new ExtensionContributionDefinition(context.PluginId.Value + ".formatter", "contoso.formatters", point.ContractVersion)
        {
            DisplayName = context.Descriptor.Name,
        },
        new PluginOperations().Add<FormatRequest, FormatResponse>("format", (request, call, token) =>
            ValueTask.FromResult(new FormatResponse(request.Text.ToUpperInvariant()))));
}
```

Contributing to a point that does not exist fails (`extension_point_not_found`), and so does an
incompatible contract version (`extension_version_incompatible`). Check `GetExtensionPoint` first,
as the template does.

## Discover and invoke (declarer)

```csharp
foreach (var contribution in runtime.Host.Extensions.GetContributions("contoso.formatters"))
{
    Console.WriteLine($"{contribution.ContributionId}: {contribution.DisplayName} by {contribution.Owner}");
}

// One contribution, typed (ModularRuntime.Sdk):
var one = await runtime.Host.Extensions.InvokeAsync<FormatRequest, FormatResponse>(
    "contoso.formatters", "com.contoso.upper.formatter", "format", new FormatRequest("hello"));

// All contributions:
ExtensionInvocationResult all = await runtime.Host.Extensions.InvokeAllAsync(
    "contoso.formatters", "format", JsonPayload.Parse("""{"text":"hello"}"""));
foreach (var result in all.Results) { /* result.Status, result.Payload, result.Provider */ }
```

## Lifecycle

- A contribution belongs to the contributing instance and disappears when it stops. A restarted
  plugin contributes again from its new instance.
- `GetExtensionPoints()`, `GetExtensionPoint(id)`, and `GetContributions(id)` return descriptors
  (plain data). Invocation goes through the registry, which never calls into a stopped plugin.
- The CLI lists points and contributions with `modularruntime extensions list`, and the desktop app
  shows them on its Extensions page.
