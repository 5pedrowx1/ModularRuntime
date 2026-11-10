# Event bus guide

Plugins (and the host) publish events on **topics** and subscribe to them. Payloads are JSON:
the publisher and the subscriber never share types or assemblies.

Template: `dotnet new modularruntime-plugin -n Greeter --kind event-publisher --topic contoso.greetings`
and `--kind event-subscriber --topic contoso.greetings`. Both are exercised end to end in scenario B
of `tests/ModularRuntime.Template.Tests`.

## Publish

```csharp
using ModularRuntime.Sdk;

public sealed record GreetingMessage(string From, string Text, int Sequence);

var result = await context.Events.PublishAsync("contoso.greetings", new GreetingMessage("me", "Hello", 1), cancellationToken: token);
context.Logger.LogInformation($"{result.Deliveries.Count} subscriber(s), {result.FailedCount} failed.");
```

Without the SDK: `context.Events.PublishAsync("contoso.greetings", JsonPayload.Parse("""{"text":"Hello"}"""))`.
Options: `EventPublishOptions { CorrelationId, Metadata }`.

`PublishAsync` delivers to every current subscriber and returns an `EventPublishResult`:
`Status`, `Deliveries` (one `EventDeliveryResult` per subscriber: `Status`, `ErrorCode`,
`Message`, `Diagnostic`), and `SucceededCount`/`FailedCount`/`CanceledCount`. Publishing with no
subscribers succeeds with zero deliveries.

## Subscribe

```csharp
// The subscriber's own type for the JSON contract (it does not reference the publisher).
public sealed record GreetingMessage(string From, string Text, int Sequence);

context.Events.Subscribe<GreetingMessage>("contoso.greetings", (message, pluginEvent, token) =>
{
    context.Logger.LogInformation($"#{message?.Sequence} from {pluginEvent.Source}: {message?.Text}");
    return ValueTask.CompletedTask;
});
```

Without the SDK: `context.Events.Subscribe(topic, (pluginEvent, token) => …)` and read
`pluginEvent.Payload` (`JsonPayload`). The event also carries `EventId`, `Source` (the publisher's
`ResourceOwner`), `Timestamp`, `CorrelationId`, and `Metadata`.

## Cancellation and errors

- The handler's token is canceled when the publisher cancels or the subscriber stops.
- A handler that throws, or a payload that does not match `T` (`PluginPayloadException`), fails
  **that delivery only**. The publisher sees it in `Deliveries`, and other subscribers are unaffected.
- Payload size and dispatch depth are limited (`payload_too_large`, `event_dispatch_depth_exceeded`).

## Ownership

The subscription (`IEventSubscription`) belongs to the subscribing instance and is removed
automatically when it stops. Dispose it earlier to unsubscribe. `context.Events.GetSubscriptions(topic)`
lists current subscriptions; the CLI shows them with `modularruntime events subscriptions`.
