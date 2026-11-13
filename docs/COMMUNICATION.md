# Communication between plugins and the host

ModularRuntime gives plugins and the hosting application five ways to communicate, all mediated
by the runtime, all owned by the instance that creates them, all cleaned up automatically when
that instance stops. Plugins never reference each other's assemblies.

| Mechanism | Shape | Data | Use it for |
|---|---|---|---|
| **Event bus** | publish/subscribe, one awaited handler per subscriber | `JsonPayload` | notifications where the publisher wants to know each delivery's outcome |
| **JSON services** | request/response by service ID and operation | `JsonPayload` | interoperable calls between any plugins, without shared types |
| **Typed services** | request/response through a C# interface | any shared type | efficient calls between plugins that share a contract assembly |
| **Binary services** | request/response by service ID and operation | bytes | buffers, frames, telemetry batches, anything large or frequent |
| **Streams** | one producer, many subscribers, bounded buffers | any shared type, `JsonPayload`, or bytes | continuous delivery with backpressure or controlled loss |
| **Extension points** | declared by a host or plugin, filled by contributions | `JsonPayload` | pluggable functionality the declarer enumerates and invokes |

Everything here is **in-process**: the daemon, the host application, and every plugin of a runtime
share one process. The daemon's clients (CLI, desktop app) talk to it over IPC with a separate,
serializable protocol ([IPC_PROTOCOL.md](IPC_PROTOCOL.md)); C# objects, delegates, and proxies are
never sent across processes.

Nothing in this document is a security boundary: plugins are trusted code running with the
process's permissions ([SECURITY.md](SECURITY.md)).

## Common rules

- **Identifiers** (topics, service IDs, stream IDs, extension point and contribution IDs,
  operation names) are *runtime names*: 1–128 characters, lowercase ASCII letters and digits,
  with `.`, `_`, `-` as separators that are never leading, trailing, or doubled. Example:
  `com.contoso.reports`.
- **Versions.** Services and streams carry a `Version`; consumers may require a minimum: same
  major and version ≥ the minimum (`service_version_incompatible`, `stream_version_incompatible`).
  Extension points use "same major, contribution minor ≤ point minor".
- **Ownership.** Every subscription, registration, stream, stream subscription, extension point,
  contribution, and tracked object belongs to the host or plugin instance that created it
  (`ResourceOwner`), appears in `context.Resources.GetResources()`, can be disposed early, and is
  released automatically when the owner stops, fails to start, or the runtime is disposed.
  See [LIFECYCLE.md](LIFECYCLE.md#teardown-order).
- **One service ID namespace.** A service ID has exactly one provider and one kind (JSON, binary,
  or typed); a second registration of any kind is `duplicate_service`. `GetServices()` lists all
  kinds with `PluginServiceDescriptor.Kind`. Invoking a service with another mechanism than its
  kind is `service_kind_mismatch`.
- **Results, not exceptions**, for runtime conditions: publications and JSON/binary invocations
  return structured results (`Succeeded`, `Failed`, `Rejected`, `Canceled` with an error code, a
  message, and a text-only `PluginDiagnostic`). Programming errors (null arguments, invalid names
  or definitions) throw `ArgumentException`; registration errors throw `PluginRuntimeException`
  with a stable `ErrorCode` (`scope_closed`, `engine_disposed`, `duplicate_service`, …). Typed
  services are the exception to the rule by design: they throw (see below).
- **Cancellation.** Every handler receives a token that is canceled when the caller cancels or
  when the handler's owner begins to stop. Stopping an owner waits for its in-flight handlers,
  bounded by `RuntimeLimits.DrainTimeout` (default 10 s); handlers that ignore cancellation keep
  running in-process.
- **Limits** (`RuntimeLimits`, configured by the host): JSON payloads ≤ `MaxPayloadBytes` (256 KiB),
  binary payloads ≤ `MaxBinaryPayloadBytes` (16 MiB, lowerable per service), metadata ≤ 32
  entries, nested publications ≤ `MaxEventDispatchDepth` (16), stream buffers ≤ `MaxStreamCapacity`
  (65 536, default 256), inspections of open values ≤ `MaxValueScanReferences` (4096) references
  and `MaxValueScanDepth` (64) hops.
- **Reentrancy.** Code running on behalf of an owner (its handlers, lifecycle methods, typed
  service methods, and the asynchronous flow they start) cannot stop that owner or dispose the
  runtime: `reentrant_lifecycle_operation` is returned instead of a deadlock.
- **Nothing of a plugin is retained** by the runtime after its instance stops: handlers,
  instances, callback targets, and buffers are dropped, descriptors and results are text, client
  handles, proxies, and callback delegates hold identifiers only. Collection of the plugin's load
  context then depends only on the plugin's own references ([LIFECYCLE.md](LIFECYCLE.md#unload-tracking)).
- **Shared values only** across typed boundaries (typed services, callbacks, streams): the
  runtime refuses, by default, any value that references an object of a plugin-private type and
  any value it cannot verify ([shared values](#shared-values)).

The `ModularRuntime.Sdk` package ([SDK.md](SDK.md)) adds typed JSON helpers, operation routers,
and background readers over these contracts; it is optional.

## Event bus

```csharp
var subscription = context.Events.Subscribe("example.greetings", (e, ct) =>
{
    context.Logger.LogInformation($"{e.Source}: {e.Payload.GetString("text")}");
    return ValueTask.CompletedTask;
});

var result = await context.Events.PublishAsync(
    "example.greetings",
    JsonPayload.Parse("""{"text":"hello"}"""),
    new EventPublishOptions { CorrelationId = "c-1" });
// result.Status; result.Deliveries: one EventDeliveryResult per subscriber
```

With the SDK: `context.Events.PublishAsync("example.greetings", new Greeting("hello"))` and
`context.Events.Subscribe<Greeting>(topic, (greeting, e, ct) => …)`.

- Topics are matched exactly (no wildcards). An event carries `EventId`, `Topic`, `Payload`,
  `Source` (the publisher's `ResourceOwner`), a UTC `Timestamp`, an optional `CorrelationId`
  (≤ 128 characters), and immutable string `Metadata`.
- **Dispatch is sequential, in subscription order**: each handler is awaited before the next
  one runs, and `PublishAsync` completes when every delivery has completed. A failing handler is
  reported in `Deliveries` (`Failed`, `handler_failed`, diagnostic) and does not stop the others.
  A slow subscriber delays the publisher; use a stream when that matters.
- Each publication dispatches to a snapshot of the subscriptions: subscriptions created during
  dispatch receive later events only; removed subscriptions receive nothing more. Subscriptions
  whose owner is shutting down are skipped.
- Publishing from inside a handler is allowed up to `MaxEventDispatchDepth`
  (`event_dispatch_depth_exceeded` beyond it).
- A canceled publication reports undelivered subscriptions as `Canceled`.

## JSON services

```csharp
// Provider
context.Services.Register(
    new PluginServiceDefinition("example.text.transform", "Text transformation", new Version(1, 0), ["uppercase", "reverse"]),
    (request, ct) => ValueTask.FromResult(PluginResponse.Success(JsonPayload.FromNode(new JsonObject { ["text"] = "…" }))));

// Consumer: by ID at call time …
var result = await context.Services.InvokeAsync("example.text.transform", "uppercase", payload, new ServiceInvocationOptions { MinimumVersion = new Version(1, 0) });
// … or through a handle bound to the current registration
var client = context.Services.GetClient("example.text.transform", minimumVersion: new Version(1, 0));
var echoed = await client!.InvokeAsync("uppercase", payload, ct);
```

With the SDK, `PluginOperations` routes operations to typed handlers and
`InvokeAsync<TRequest, TResponse>` deserializes the answer ([SDK.md](SDK.md)).

| Rejected before the handler runs | Code |
|---|---|
| unknown service | `service_not_found` |
| provider shutting down | `service_unavailable` |
| the registration a client is bound to was removed | `service_removed` |
| undeclared operation | `operation_not_supported` |
| wrong mechanism for the service's kind | `service_kind_mismatch` |
| request larger than `MaxPayloadBytes` | `payload_too_large` |
| the caller has stopped | `scope_closed` |

| Handler outcome | Code |
|---|---|
| throws | `handler_failed`, with a diagnostic (also logged as a warning) |
| returns `PluginResponse.Failure(code, message)` | that code |
| returns `null` | `invalid_response` |
| response larger than `MaxPayloadBytes` | `payload_too_large` |
| canceled | `operation_canceled` |

`IPluginServiceClient` stores only the service ID and registration ID: a consumer can keep it
after the provider stops without keeping the provider alive. Once the bound registration is
removed the handle stays inactive even if another provider registers the same ID (`IsAvailable`
is false, calls answer `service_removed`); resolve again to bind to the new provider.

## Typed services

A provider registers an object that implements a C# interface; consumers call that interface
through a proxy the runtime generates. No JSON, no copies, direct calls.

```csharp
// Shared contract assembly (Contoso.Telemetry.Contracts.dll), referenced by both plugins
public interface ITelemetryService
{
    long Record(TelemetrySample sample);
    Task FlushAsync(CancellationToken cancellationToken);
}
public readonly record struct TelemetrySample(string Metric, double Value, DateTimeOffset At);

// Provider plugin
context.TypedServices.Register<ITelemetryService>(
    new TypedServiceDefinition("contoso.telemetry", "Telemetry", new Version(1, 0)), this);

// Consumer plugin
var telemetry = context.TypedServices.Resolve<ITelemetryService>("contoso.telemetry", new Version(1, 0));
telemetry?.Record(new TelemetrySample("cpu", 0.42, DateTimeOffset.UtcNow));
```

`GetClient<T>()` returns the same proxy together with `IsAvailable`, `RegistrationId`, and
`GetDescriptor()`.

### Shared contract assemblies

Provider and consumer live in different collectible load contexts, so an interface compiled into
each plugin's private copy of an assembly would be two different `Type`s. The contract therefore
lives in its own assembly, which both plugins **declare** in `plugin.json`:

```json
"sharedContracts": ["Contoso.Telemetry.Contracts"]
```

The runtime then loads that assembly **once** per version line, into a collectible *shared contract
context*, from the bytes of the first plugin that needs it (after that plugin's installation was
verified), and every plugin that declares it resolves the assembly from there. The context is
unloaded when its last user stops. Nothing is ever loaded into the default load context, and no
plugin file stays locked by a shared context.

Rules for a contract assembly:

- it ships with every plugin that uses it (a normal copied dependency; `dotnet modularruntime pack`
  includes it) and is declared by its simple name, without path, extension, or version;
- it references only the framework, `ModularRuntime.Contracts`, and other shared contracts
  declared by the same plugin; a reference to a plugin's private assembly is refused at load
  (`shared_contract_dependency`), because it would leak that assembly into every plugin sharing
  the contract;
- it is versioned by `AssemblyVersion`: the same major is compatible. A plugin binds to a loaded
  copy of the same major whose version is ≥ the version the plugin was compiled against; if an
  older copy is already loaded for other plugins, the newer plugin fails to load with
  `shared_contract_version_conflict` (stop or update the plugins holding the old copy, or start
  the newest one first). Different majors coexist in separate contexts;
- `ModularRuntime.*` assemblies cannot be declared (`ModularRuntime.Contracts` is always shared).

A plugin that references the contract without declaring it gets its private copy and, on
`Resolve<T>`, a `typed_service_contract_mismatch` that says so. `PluginRuntime.GetSharedContracts()`
(CLI `streams list`, desktop Streams page) shows what is loaded and who uses it.

### Contract rules

The contract is a **public interface** (public at every nesting level), closed if generic, without
static abstract or virtual members. Methods, properties, events, generic methods, `ref`/`out`
parameters, and default interface methods are supported (a default implementation runs on the
caller side, inside the proxy). Every member is forwarded, including `Dispose` if the contract
has it, so keep lifetime-changing members out of contracts.

At registration the runtime validates the contract itself and every type its signatures expose
(parameters, results, the `T` of `Task<T>`/`ValueTask<T>`): a type that is not shared, or that
cannot be analyzed, is `typed_service_invalid_contract` (in `Reject` mode; a warning in `Warn`
mode). A contract type private to the plugin (the assembly was not declared in `sharedContracts`)
is refused with that guidance.

### Shared values

Everything that crosses a typed boundary (typed-service arguments and results, callback
arguments and results, stream items) must be a **shared value**: its type, and the type of
everything it references, comes from the contract assembly, from `ModularRuntime.Contracts`, or
from the framework. An object whose type is private to a plugin keeps that plugin's load context
alive for as long as the other side holds it, and the runtime cannot take it back. The one
parameter that is not a crossing is a `CancellationToken`: the callee never receives the caller's
token (see [Call semantics](#call-semantics)), so it is neither inspected nor validated.

The runtime enforces this by construction (`RuntimeLimits.TypedValueCheck`, **`Reject` by
default**). Every type is classified once (cached with weak keys, so plugin types are never
retained by the cache):

| Class | What it means | Examples | Cost at a crossing |
|---|---|---|---|
| **closed** | nothing a value of the type can reference is private: the type is shared, sealed (or a value type), and so are all its instance fields, recursively | primitives, `string`, `Guid`, `DateTimeOffset`, `Version`, `Uri`, `JsonPayload`, a `sealed record Sample(string Metric, double Value)`, `string[]`, `ImmutableArray<string>`, `List<string>` (as an instance), `KeyValuePair<string, string>` | none: no inspection |
| **open** | the type does not settle the question; the instance is inspected | `object`, interfaces, non-sealed classes, delegates, exceptions, arrays and collections of open types, a record with an `object`, interface, or delegate field, `Dictionary<,>` and sets (their comparer is an object) | one lookup when the runtime type is closed; otherwise a bounded inspection |
| **private** | the type, or a type it is constructed from, is not shared | a plugin's own classes, `List<MyPluginType>`, `Action<MyPluginType>`, a closure class capturing a plugin object | refused |

An **inspection** reads state only and never calls plugin code: the instance fields of the
object (through generated field readers, no reflection per value), the elements of arrays and
of framework collections together with their comparers, the invocation targets of delegates
(the declaring type of each method and the target object), the fields, inner exceptions, and
`Data` of exceptions, the type a reflection object represents (`Type`, `MemberInfo`, handles),
the manager behind a `Memory<T>`, the target of a `GCHandle`. It is iterative, enters every
object once (cyclic graphs terminate), never enumerates lazy sequences (an iterator is inspected
through its fields: the delegate it holds, the object it captured), and is bounded by
`RuntimeLimits.MaxValueScanReferences` (4096 references) and `MaxValueScanDepth` (64 hops from the
value). A value the inspection cannot finish is **refused as `value_not_verifiable`**: the
runtime fails closed rather than guess. Prefer closed types (typed collections instead of
`List<object>`, sealed records instead of interfaces) so that nothing is inspected at all; the
limits are for the rare contract that legitimately passes large open graphs.

A refusal throws `TypedServiceException` (`value_not_shared` or `value_not_verifiable`) whose
message names the path to the offending reference, for example
`Typed service 'contoso.telemetry', argument 'batch' → field 'Items' → element [3]: a value of
type 'Contoso.Plugin.Sample' from assembly 'Contoso.Plugin' is not a shared type`. Stream writes
throw `PluginRuntimeException` with the same codes.

Rules for contract authors:

- **DTOs**: sealed records or structs whose members are closed types; typed arrays and lists;
  immutable once handed over. A mutable object is verified when it crosses, not afterwards:
  what the sender stores into it later is the sender's responsibility.
- **Interfaces, `object`, delegates in a contract** are allowed; the instance is checked at every
  crossing. A delegate must target shared code: a static lambda in the contract assembly, a
  method of a shared object, or a callback registration (below). A lambda of plugin code, even
  one that captures nothing, is plugin code; and C# hoists every captured local of a scope into
  one closure object, so a lambda that captures only shared values is still refused when a
  sibling lambda of the same scope captured a plugin object.
- **Exceptions** must be shared all the way down (type, fields, inner exceptions, `Data`) to
  propagate unchanged; otherwise they are converted to text (see call semantics).
- **Reflection objects** are judged by what they represent: `typeof(MyPluginType)` is private.

Modes: `Reject` (default) refuses offenders and unverifiable values and validates contracts at
registration. `Warn` verifies the same way, logs once per offending type (or per unverifiable
root type), and lets the value pass: the retention still happens, so use it only while migrating
a plugin and watch the log. `None` skips every check, including registration validation; only
for hosts that trust every plugin's contract discipline completely, and the fastest option by a
few nanoseconds per call ([BENCHMARKS.md](BENCHMARKS.md)).

### Callbacks

A consumer that needs the provider to call it back registers the callback with the runtime and
hands over the registration's delegate:

```csharp
// Consumer
var registration = context.TypedServices.RegisterCallback<Action<TelemetrySample>>("sample-sink", OnSample);
telemetry.Subscribe(registration.Callback);     // a shared delegate of the same type

// Provider: invokes it like any delegate; after the consumer stops it throws callback_released
```

`registration.Callback` is a runtime-generated delegate: it holds nothing of the consumer (its
target is a runtime object, its method has no declaring type), resolves the registration on
every invocation, admits the call through the consumer's scope (drained and canceled when the
consumer stops, like a typed-service call), checks arguments and results as shared values, and
translates exceptions exactly as a proxy does. The registration is an owned resource
(`ResourceKind.Callback`): disposing it, or the consumer stopping, drops the target; a provider
that still holds the delegate gets `TypedServiceException` (`callback_unavailable` while the
owner shuts down, `callback_released` afterwards) and keeps nothing alive. The delegate type must
be public, closed, from the framework or a contract assembly, and its signature shared (validated
like a contract). A `CancellationToken` parameter is replaced by a token owned by the call and
linked to the owner's lifetime, as for a typed-service call.

### Call semantics

Each call through a proxy:

1. resolves the registration (`service_removed` after the provider stopped; `engine_disposed`;
   `scope_closed` when the caller itself has stopped);
2. is admitted through the provider's scope (`service_unavailable` while the provider is
   shutting down) and counted for draining;
3. replaces every `CancellationToken` parameter: the provider receives the provider's own scope
   token when the caller's token cannot be canceled, otherwise a token owned by the call that is
   canceled when the caller's token or the provider's scope is canceled, so in-flight calls are
   canceled when the provider stops (`operation_canceled`). The link is undone when the call
   ends: a provider that keeps the token keeps a detached source and nothing of the caller (not
   the caller's source, not the callbacks registered on it), which is why the caller's token is
   not inspected;
4. records a reentrancy frame (thread-local for synchronous calls, flowing with the asynchronous
   context for `Task`/`ValueTask` methods);
5. calls the provider's object directly, without boxing or reflection; for `Task` and `ValueTask`
   results the admission ends when the task completes.

Failures are **exceptions**, because the contract is a C# interface: exceptions whose type is
shared (contract assembly, framework, `ModularRuntime.Contracts`) and whose whole content is shared
too (their own fields, inner exceptions, the parts of an `AggregateException`, the keys and values
of `Data`), and caller cancellations, propagate unchanged; any other exception is replaced by
`TypedServiceException` (`handler_failed`) carrying a text-only `PluginDiagnostic` and the path to
the offending reference, and logged. Runtime conditions throw `TypedServiceException` with the
codes above; methods returning `Task`/`ValueTask` report them through the task. An exception that
propagates unchanged was thrown by provider code and references it through its stack trace: the
runtime keeps a collectible assembly alive while such an exception object is reachable, so a
consumer that stores the exception object (rather than its text) keeps the provider's load context
alive until it drops it.

A proxy never references the provider's object, so a consumer may keep it after the provider
stopped; it is permanently bound to that registration (like `IPluginServiceClient`). There are no
implicit timeouts: a provider that blocks blocks its caller. The cost of a call is a few
nanoseconds above a direct interface call (see [BENCHMARKS.md](BENCHMARKS.md)).

## Binary services

```csharp
// Provider
context.Services.RegisterBinary(
    new BinaryServiceDefinition("example.checksum", "Checksums", new Version(1, 0), ["crc32"]) { MaxRequestBytes = 1024 * 1024 },
    (request, ct) => ValueTask.FromResult(BinaryResponse.Success(Crc32(request.Payload.Span))));

// Consumer
using var result = await context.Services.InvokeBinaryAsync("example.checksum", "crc32", bytes, cancellationToken: ct);
if (result.IsSuccess) { Use(result.Payload.Span); }
```

Binary services follow the JSON service rules (same registry, kinds, admission, cancellation,
error codes, `IPluginServiceClient.InvokeBinaryAsync`) with explicit **buffer ownership**:

- The **request** `ReadOnlyMemory<byte>` is *borrowed* by the handler until the call completes.
  The runtime does not copy it; the caller must not modify it while the call is in flight, and a
  handler that keeps bytes beyond the call must copy them.
- The **response** is *transferred* to the caller: `BinaryResponse.Success(memory)` for bytes the
  handler will never touch again (a fresh array, an immutable buffer), or
  `BinaryResponse.Success(IMemoryOwner<byte> owner, int length)` for a pooled buffer. The caller
  disposes `BinaryInvocationResult` when it is done; disposal returns a pooled buffer to its pool
  and is otherwise a no-op. A rented buffer must never be returned to a pool by the handler after
  it was handed to the runtime.
- Sizes: requests above the service's `MaxRequestBytes` are rejected (`payload_too_large`),
  responses above `MaxResponseBytes` are failed and their buffer is disposed; both default to
  `RuntimeLimits.MaxBinaryPayloadBytes` and can only be lowered per service. The runtime does not
  interpret the bytes; the service validates them.

With the SDK, `BinaryOperations` routes operations and `context.Services.RegisterBinary(id, name,
version, operations)` registers them.

## Streams

```csharp
// Producer
var samples = context.Streams.Create<TelemetrySample>(
    new StreamDefinition("contoso.samples", "Samples", new Version(1, 0)) { DefaultCapacity = 64, DefaultFullMode = StreamFullMode.DropOldest });
await samples.WriteAsync(sample, ct);      // or samples.TryWrite(sample)
samples.Complete();                        // optional: subscribers finish reading, then complete

// Subscriber
var subscription = context.Streams.Subscribe<TelemetrySample>("contoso.samples", new StreamSubscribeOptions { Capacity = 16, FullMode = StreamFullMode.Wait });
await foreach (var message in subscription!.ReadAllAsync(context.StoppingToken))
{
    Use(message.Sequence, message.Item);
}
// subscription.Completion: why the reader completed (stream_completed, stream_removed, subscription_disposed)
```

With the SDK, `context.Consume<T>(streamId, handler)` reads in the background until the stream
completes or the plugin stops; `ConsumeJson<T>` and `WriteJsonAsync<T>` serve `JsonPayload` streams.

- One producer per stream ID (`duplicate_stream`); any number of subscribers, each with its own
  bounded buffer (`Capacity`, 1 to `MaxStreamCapacity`) and policy:
  - `Wait` (default): the producer's `WriteAsync` waits for room; the slowest subscriber throttles
    the producer; `TryWrite` returns `false` when such a subscriber is full (that subscriber
    misses the item; the others receive it);
  - `DropOldest` / `DropNewest`: the producer never waits; the subscriber loses items and sees the
    loss in `DroppedCount` and as gaps in the message `Sequence` numbers.
- Items are delivered to every subscriber **without copying**, so they must be immutable once
  written, and they must be shared values ([above](#shared-values)): a private item type is
  refused at `Create<T>` (`value_not_shared`); when the item type is open (`object`, an
  interface, a non-sealed class, a record with such a field) every written item is inspected and
  a write that fails throws `PluginRuntimeException` (`value_not_shared`, `value_not_verifiable`).
  A closed item type costs nothing per write. For `ReadOnlyMemory<byte>` streams the runtime
  copies each item once on write by default (`CopyBinaryItems`), so producers may reuse their
  buffers and nothing is inspected; a stream created with `CopyBinaryItems = false` shares the
  producer's memory read-only, inspects its backing object (a `MemoryManager<byte>` of a plugin is
  refused), and the producer must never modify or pool it afterwards.
- `WriteAsync` returns `false` once the stream is completed or removed; it throws
  `OperationCanceledException` when the producer's token is canceled while waiting.
- When the producer stops (or disposes the stream), every subscription completes with
  `stream_removed` after its buffered items and is released from its owner; subscribing to a
  missing stream returns `null`; subscribing with the wrong item type throws
  `stream_item_type_mismatch`. When a subscriber stops, its subscription is detached and the
  producer is no longer throttled by it.
- No owner code runs inside the stream registry, so streams need no draining: stopping a plugin
  never waits for a reader.

Streams complement the event bus: the bus delivers one event to every subscriber sequentially and
reports each delivery; a stream decouples producer and subscribers with explicit backpressure or
loss and no per-item result.

## Extension points

```csharp
// Host (or a plugin) declares a point
host.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition(
    "example.document.exporters", "Document exporters", new Version(1, 0)) { Operations = ["export"] });

// A plugin contributes (check the point exists first)
if (context.Extensions.GetExtensionPoint("example.document.exporters") is { } point)
{
    context.Extensions.Contribute(
        new ExtensionContributionDefinition("markdown", "example.document.exporters", point.ContractVersion) { DisplayName = "Markdown exporter" },
        ExportAsync);
}

// The declarer discovers and invokes
var contributions = host.Extensions.GetContributions("example.document.exporters");
var one = await host.Extensions.InvokeAsync("example.document.exporters", "markdown", "export", payload);
var all = await host.Extensions.InvokeAllAsync("example.document.exporters", "export", payload);
```

- Points are declared by the host or by plugins. A contribution targets an existing point with a
  compatible contract version (`extension_point_not_found`, `extension_point_unavailable`,
  `extension_version_incompatible`, `duplicate_contribution` within a point). A point may restrict
  the operations its contributions accept (an empty list means unrestricted).
- `InvokeAsync` calls one contribution; `InvokeAllAsync` calls every active contribution
  sequentially in contribution order and aggregates the results; a failing contribution does not
  stop the others. Contributions use the same invocation engine, payload limits, and outcome codes
  as JSON services (`contribution_not_found`, `contribution_unavailable`, `handler_failed`, …).
- If the point's owner stops or disposes the registration, the point and all its contributions are
  removed (the contributors' handles become inactive). If a contributor stops, only its
  contributions are removed. Descriptors are immutable snapshots.

## Choosing a mechanism

- Unknown or external consumers, forward compatibility, anything the CLI or another language may
  need to read one day: **JSON** (events or services).
- Two or more plugins you control that call each other often, with rich signatures: a **typed
  service** over a shared contract assembly. Keep contracts small and additive; bump the major for
  breaking changes.
- Large or frequent opaque data between two parties: a **binary service**.
- Continuous data to several readers, or a producer that must not be slowed by a consumer: a
  **stream** (typed, JSON, or binary).
- Functionality that the host enumerates and invokes, filled by whichever plugins are present: an
  **extension point**.

## Concurrency and memory model

- **Locks.** One private lock per component (event bus, service registry, extension registry,
  stream registry, each stream, each ownership scope, each plugin slot, the engine registry);
  no global lock; no lock is held while another is acquired; no owner code (handlers, typed
  service methods, cancellation callbacks, disposables, loggers) runs under a runtime lock.
- **Admission.** Every invocation of a handler or typed method is admitted through its owner's
  scope (an in-flight counter that rejects new work once the owner is closing) and the
  registration's release flag is checked again after admission. This defines when a removal
  becomes effective and lets teardown drain exactly the invocations that started.
- **Release** of an owned resource is one shared operation: concurrent disposals, scope close,
  and extension point removal await the same completion, and a resource leaves its scope only
  when its release has finished, so unload is never requested while cleanup is pending.
- **Registries keep only what is active**: a handler delegate, a typed instance, or a stream's
  subscriber array while registered; release drops it. Descriptors, results, events,
  `ResourceInfo`, log entries, and `PluginErrorInfo` contain only text, numbers, GUIDs, `Version`,
  and `ModularRuntime.Contracts` records; exceptions become `PluginDiagnostic` immediately.
- **Stack hygiene.** The runtime clears locals that could hold plugin objects before continuing,
  never awaits inside a `catch` that received a plugin exception, and resumes callers on a fresh
  thread-pool stack after plugin code completed asynchronously (`ThreadPoolHop`; typed asynchronous
  calls yield to the pool for the same reason), so callers never continue on top of plugin frames.
- **Typed proxies** are emitted into a collectible dynamic assembly created in the load context
  of the contract assembly, so they are collected together with that context; the engine holds
  proxy types and modules through weak keys only.
- **Unload is cooperative**: the runtime releases everything it manages; a plugin that keeps
  threads running, static roots, `GCHandle`s, non-cooperative handlers, or callbacks given to
  other plugins keeps its context alive until it lets go.

## Error codes

Codes appear in results (`ErrorCode`) and in `PluginRuntimeException.ErrorCode` /
`TypedServiceException.ErrorCode`; the full list is `PluginErrorCodes`.

| Area | Codes |
|---|---|
| ownership and validation | `scope_closed`, `engine_disposed`, `invalid_identifier`, `invalid_metadata`, `invalid_payload`, `payload_too_large`, `value_not_shared`, `value_not_verifiable` |
| invocation | `handler_failed`, `invalid_response`, `operation_not_supported`, `operation_canceled`, `reentrant_lifecycle_operation`, `drain_timeout` |
| events | `event_dispatch_depth_exceeded` |
| services | `duplicate_service`, `service_not_found`, `service_unavailable`, `service_removed`, `service_version_incompatible`, `service_kind_mismatch`, `typed_service_invalid_contract`, `typed_service_contract_mismatch`, `callback_unavailable`, `callback_released` |
| streams | `duplicate_stream`, `stream_not_found`, `stream_unavailable`, `stream_removed`, `stream_completed`, `stream_item_type_mismatch`, `stream_version_incompatible`, `subscription_disposed` |
| extensions | `duplicate_extension_point`, `extension_point_not_found`, `extension_point_unavailable`, `extension_version_incompatible`, `duplicate_contribution`, `contribution_not_found`, `contribution_unavailable` |
| shared contracts (loader) | `shared_contract_missing`, `shared_contract_invalid`, `shared_contract_version_conflict`, `shared_contract_dependency` |

## Limitations

- No permissions, quotas, or rate limits per plugin; any plugin may use any service, topic,
  stream, or point.
- No timeouts other than the drain timeout; a blocking provider blocks its caller.
- Event dispatch is sequential; topics have no wildcards; JSON payloads have no schema validation.
- Typed services cannot cross processes or load contexts other than through a declared shared
  contract; a contract compiled into the host application (default context) is a different
  identity from the shared one and is reported as `typed_service_contract_mismatch`.
- Reentrancy detection follows the asynchronous flow for asynchronous calls and the thread for
  synchronous typed calls, so background work started from a handler counts as that owner's flow,
  while a synchronous typed call that spawns a thread does not.
- The shared-value check decides from types where it can and inspects instances where it must,
  within `MaxValueScanReferences` and `MaxValueScanDepth`; what it cannot finish it refuses. It
  reads state, so a mutable object verified at a crossing can be changed afterwards by whoever
  still holds it; a shared exception that propagates unchanged references the thrower's code
  through its stack trace; static fields of contract assemblies, `ConditionalWeakTable` values,
  thread statics, and objects reachable only through unmanaged handles are not inspected; the
  CLR itself retains a collectible context through live threads, timers, and continuations of
  plugin code. Unload remains cooperative: `PluginRuntime.WaitForUnloadAsync` is the place to
  catch what the check cannot see.
- Declarative dependencies between plugins (requires/optional, derived start order, cascading
  stop) are not implemented; see [ROADMAP.md](ROADMAP.md).
