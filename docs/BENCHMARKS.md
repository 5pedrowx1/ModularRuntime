# Benchmarks

`benchmarks/ModularRuntime.Benchmarks` measures the communication mechanisms with
[BenchmarkDotNet](https://benchmarkdotnet.org/) 0.15.8. The project is not part of the test suite.

```powershell
dotnet run -c Release --project benchmarks/ModularRuntime.Benchmarks -- --filter "*"
dotnet run -c Release --project benchmarks/ModularRuntime.Benchmarks -- --filter "*ServiceCall*"
```

Results land in `BenchmarkDotNet.Artifacts/` (ignored by Git). Each benchmark class states what it
measures; "v0.6 algorithm" rows reproduce the previous implementation for a like-for-like
comparison, because the previous code is not available in the same process.

## What is measured

| Class | Scenario |
|---|---|
| `JsonPayloadBenchmarks` | creating and reading `JsonPayload` (small object, 80 KiB batch), `GetString`, SDK `ToPayload<T>` / `FromPayload<T>` |
| `ServiceCallBenchmarks` | one request/response, host to host in one engine: direct interface call (floor), typed proxy (sync, `Task<T>`, `ValueTask<T>`, with and without a cancellable token, a closed record, an open record, a `List<object>` argument), binary service, JSON service (raw and typed SDK call); with the shared-value check in `Reject` (default) and `None` |
| `EventStreamBenchmarks` | event bus `PublishAsync` versus stream writes (`JsonPayload` and a struct), one and four subscribers, plus 10 000 items from four concurrent producers |
| `RegistrationBenchmarks` | register and dispose a JSON service, a typed service (proxy type cached), a stream with one subscription |

Everything runs in the default load context: these are the runtime's own costs, without the
plugin load contexts, which add nothing per call (a proxy or handler call does not cross a context
boundary at run time).

## Methodology

- `MemoryDiagnoser` on every class (allocated bytes per operation).
- BenchmarkDotNet default job: pilot, warm-up, 15+ measured iterations, outlier removal; `Mean`
  with `Error` (99.9% confidence half-width) and `StdDev`. The stream and typed benchmarks
  involve thread-pool scheduling, so their standard deviations are naturally higher.
- Workstation GC, `TieredPGO` on (the project settings); the machine is otherwise idle. A
  process that starts during a run shows up as multimodal distributions (BenchmarkDotNet warns
  with `MValue` ≥ 2.8), iteration counts that hit the maximum of 100 without converging, and
  slowdowns in rows whose code did not change; compare `Median` with `Mean` and discard such a
  run (the 2026-11-12 run below lost its second half that way).
- The direct call is a real call (the provider's methods are `NoInlining` and the operands come
  from fields, so the JIT cannot fold `2 + 3` to a constant; the proxies call the same methods,
  so every row carries the same provider work), but it is below what the harness can resolve:
  BenchmarkDotNet subtracts the empty-loop overhead from the workload loop, and here both are
  about 2 ns per operation (in the 2026-11-12 run, 1.9 to 2.8 ns of workload against 1.2 to
  1.8 ns of overhead, depending on the process), which it reports as 0 to 0.7 ns with a
  `ZeroMeasurement` warning. The row is therefore an order of magnitude ("under 1 ns"), not a
  number, and it is not the baseline: the `Ratio` column is against the synchronous proxy call of
  the same mode and reads as "times a bare proxy call".
- The `None` and `Reject` cases do the same functional work: the proxy code does not depend
  on the mode (which parameters and results carry a check is decided from the static types
  by `InvocationEmitter.ShouldCheck`, policy-free), the same instances cross the boundary, and in
  `None` the check returns after one mode test. `ServiceCallBenchmarks` runs the two cases of
  each method back to back (a custom execution order), each in its own process, so a change
  of the machine's state disturbs one pair rather than one column. What the closed-record,
  open-record and `List<object>` rows exercise is pinned by
  `SharedValueCheckTests.BenchmarkShapes_ExerciseTheChecksTheirDescriptionsClaim`: no check
  emitted for the closed record; two references visited for the open record (the record and
  its `string[]`, whose elements are never visited); nine for the list (the list and its eight
  elements).
- The numbers below are from one run; treat them as orders of magnitude and ratios, not as
  guarantees. Re-run on your hardware before deciding anything from them.

## Environment of the recorded run

```text
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9711)
AMD Ryzen 5 5600G with Radeon Graphics 3.90GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.403; .NET 10.0.14 (10.0.1426.52326), X64 RyuJIT x86-64-v3
Date: 2026-11-11 (payloads, events, registration), 2026-11-12 (services); ModularRuntime 0.7.0 (working tree before commit)
```

## Results

### JSON payloads

| Method | Mean | Allocated |
|---|---:|---:|
| Parse small (v0.6 algorithm: `JsonDocument` validation) | 239 ns | 72 B |
| Parse small (string) | 122 ns | 104 B |
| Parse large 80 KiB (v0.6 algorithm) | 260 µs | 168 B |
| Parse large 80 KiB (string) | 208 µs | 88 968 B |
| Parse large 80 KiB (UTF-8 bytes) | 207 µs | 88 968 B |
| GetString on large (v0.6: full document) | 261 µs | 264 B |
| GetString on large (forward reader) | 17 ns | 0 B |
| SDK ToPayload<T> (v0.6: serialize to string + parse) | 389 ns | 208 B |
| SDK ToPayload<T> (serialize to UTF-8) | 355 ns | 176 B |
| SDK FromPayload<T> (v0.6: deserialize string) | 349 ns | 176 B |
| SDK FromPayload<T> (deserialize UTF-8) | 339 ns | 176 B |

Reading: validation with a forward-only reader halves the cost of a small payload and removes the
pooled `JsonDocument`; the new payload stores a UTF-8 copy (the 88 KiB allocation for the large
case, which the v0.6 design paid later, at every `ToDocument`, as a transcoding instead); reading
one top-level property no longer builds a document. The SDK gains little on the serializer
calls themselves (about 10% and one string less per `ToPayload`): `System.Text.Json` was
already fast on strings, and the win of the UTF-8 path is the absence of transcoding at the
runtime boundary, not the serializer.

### Services: one call, host to host

Default job, 2026-11-12 (18:28), machine idle, the `None` and `Reject` case of each method run
back to back; no row was flagged multimodal, and the 99.9% half-widths are 0.3 to 3 ns on the
typed rows and 10 to 17 ns on the JSON rows. Measured on the code before the cancellation-token
fix described below.

| Method | Mean (None) | Mean (Reject) | Allocated |
|---|---:|---:|---:|
| Direct interface call (no runtime) | under 1 ns | under 1 ns | 0 B |
| Typed service: sync call through proxy | 48.7 ns | 46.1 ns | 0 B |
| Typed service: `Task<int>` call through proxy | 103.8 ns | 152.5 ns | 152 B |
| Typed service: `Task<int>` with a cancellable token | 142.8 ns | 327.1 ns | 232 B |
| Typed service: `ValueTask<int>` call through proxy | 76.2 ns | 78.0 ns | 128 B |
| Typed service: closed record argument and result (no inspection) | 46.5 ns | 46.0 ns | 0 B |
| Typed service: open record (interface field) argument and result | 61.5 ns | 184.8 ns | 0 B |
| Typed service: `object` argument carrying `List<object>` of 8 strings | 60.3 ns | 327.1 ns | 0 B |
| Binary service: `InvokeBinaryAsync` (8 bytes) | 151.3 ns | 150.7 ns | 352 B |
| JSON service: `InvokeAsync` (raw payload) | 615.9 ns | 618.9 ns | 592 B |
| JSON service: SDK `InvokeAsync<TRequest, TResponse>` | 1 053.5 ns | 1 077.1 ns | 888 B |

Reading the `None` column: a synchronous typed call costs about 47 ns and allocates nothing:
the proxy resolves the registration, enters the provider's scope, pushes a thread-local
reentrancy frame, calls the provider's object directly and translates exceptions. A `Task<int>`
call adds about 55 ns and 128 B for the flowing frame (an execution-context change) and the
task observation (the `Task<int>` for a small value is cached by the framework); a cancellable
token adds the call's own cancellation source and its two registrations; a `ValueTask<int>`
that completes synchronously only pays the frame. A binary call is about three times a sync
typed call: the request/response path admits the call, wraps the request, and materializes a
result object. The raw JSON row is not only the runtime: its handler is a `PluginOperations`
operation, so it already deserializes the request and serializes the response (about 550 ns of
the 616, consistent with the `FromPayload`/`ToPayload` rows above); the SDK row adds the same
pair on the caller's side. Nothing here measures the plugin's own work, which is the same in
every mechanism. The two earlier runs that measured the sync call at 56.7 ns (2026-11-11) and
74.9 ns (2026-11-12, 17:33) were machine state, not code: the paired run gives 46 to 49 ns.

#### What `Reject` costs, row by row

- Sync call, closed record, binary, JSON: nothing (the differences are within the error).
- Open record: +123 ns for two crossings (argument and result), about 60 ns per inspection of
  a flat record: renting the per-thread scan state, one type lookup for the record, one emitted
  field read, one type lookup for the `string[]` (closed, so its elements are never visited),
  returning the state.
- `List<object>` of 8 strings: +267 ns for two crossings, about 60 ns per scan plus about 9 ns
  per element (one weak-table lookup of the element's type each).
- `Task<int>` (+49 ns) and `Task<int>` with a cancellable token (+184 ns): neither lifecycle
  control nor cancellation, which cost the same in both modes and are already in the `None`
  column, but the inspection of the `CancellationToken` argument. The token is a struct holding
  its source, a non-sealed class with registration lists, so the proxy treated it as an open
  argument and scanned it on every call: for `CancellationToken.None` one scan of an empty
  struct (+49 ns); for a live token the whole source and its registration nodes (+184 ns), and a
  token whose source carried a plugin callback was even refused (`value_not_shared`). That
  inspection verified a value that does not cross: the proxy never hands the caller's token to
  the provider, it links it. Fixed after this run: the proxy no longer checks `CancellationToken`
  parameters, and the link is made with a source owned by the call whose registrations are
  disposed when the call ends, so a provider that keeps the token retains nothing of the caller
  (the framework's linked source kept, through its registrations, the caller's source and the
  callbacks registered on it, even after disposal). Regression tests:
  `SharedValueCheckTests.CancellationTokens_AreNotInspected_BecauseTheCalleeReceivesACallOwnedToken`
  and `CalleeKeepingTheToken_DoesNotRetainTheCallerSourceOrItsCallbacks`.
- `ValueTask<int>` (+1.8 ns): no token parameter, no difference; the control row for the
  attribution above.

After the fix, a `ShortRun` of the typed rows (3 iterations, so the absolute values are rough
and the half-widths tens of nanoseconds; the pairs are what to read):

| Method (ShortRun, after the fix) | Mean (None) | Mean (Reject) | Allocated |
|---|---:|---:|---:|
| Typed service: sync call through proxy | 54.1 ns | 56.4 ns | 0 B |
| Typed service: `Task<int>` call through proxy | 115.4 ns | 112.4 ns | 128 B |
| Typed service: `Task<int>` with a cancellable token | 208.6 ns | 190.7 ns | 176 B |
| Typed service: `ValueTask<int>` call through proxy | 109.5 ns | 96.0 ns | 128 B |
| Typed service: open record (interface field) argument and result | 69.3 ns | 232.4 ns | 0 B |

The asynchronous rows no longer differ between the modes, and they allocate less in both: the
24 B that boxed the token for the inspection (emitted whatever the mode) are gone, and the
call's own source with two registrations (176 B) replaces the framework's linked source
(232 B). The remaining cost of `Reject` is the inspection of open values, which is the design:
the way to keep typed calls at the floor is to type the contract with closed types. Two
micro-optimizations were identified and not applied, for lack of evidence that they matter: the
root's type is looked up twice per scan (once by the check, once by the scan), and a collection
of elements of one type looks each element's type up separately. The next full measurement:

```powershell
dotnet run -c Release --project benchmarks/ModularRuntime.Benchmarks -- --filter "*ServiceCall*"
```

### Event bus versus streams

| Method | Subscribers | Mean | Allocated |
|---|---:|---:|---:|
| Event bus: `PublishAsync` one JSON event | 1 | 306 ns | 536 B |
| Stream<`JsonPayload`>: `WriteAsync` one item | 1 | 172 ns | 2 B |
| Stream<struct>: `WriteAsync` one item | 1 | 135 ns | 1 B |
| Stream<struct>: `TryWrite` one item | 1 | 104 ns | 1 B |
| Stream<struct>: 10 000 items from 4 concurrent producers | 1 | 3.24 ms (324 ns/item) | 1.14 MB |
| Event bus: `PublishAsync` one JSON event | 4 | 602 ns | 1 208 B |
| Stream<`JsonPayload`>: `WriteAsync` one item | 4 | 2.04 µs | 355 B |
| Stream<struct>: `WriteAsync` one item | 4 | 1.99 µs | 347 B |
| Stream<struct>: `TryWrite` one item | 4 | 1.88 µs | 323 B |
| Stream<struct>: 10 000 items from 4 concurrent producers | 4 | 4.82 ms (482 ns/item) | 0.99 MB |

Reading: with one subscriber a stream write costs half an event publication and allocates
nothing (the item goes straight into the subscriber's channel). With four subscribers the
comparison is not like for like: the bus calls four trivial handlers synchronously on the
publisher's thread, while a stream wakes four reader tasks on the thread pool for every item
(the readers in this benchmark are always waiting, so every write pays a wake-up). A stream is
the right tool when consumers must not run on the producer's thread or must not slow it down;
when a producer only needs to notify fast handlers, the bus is cheaper. Sustained throughput with
four concurrent producers and a bounded buffer of 1024 is about 2–3 million items per second.

### Registration

| Method | Mean | Allocated |
|---|---:|---:|
| Register + dispose a JSON service | 623 ns | 1.22 KB |
| Register + dispose a typed service and resolve one proxy (proxy type cached) | 3.28 µs | 3.16 KB |
| Create a stream, subscribe, dispose both | 1.33 µs | 2.75 KB |

Reading: registration costs are dominated by validation copies, descriptors, and the ownership
bookkeeping; a typed registration also validates the contract type by reflection and creates a
proxy instance (the proxy *type* is emitted once per contract and cached). None of this is on a
hot path.

## Reading the results together

- A typed-service call is the only mechanism whose cost is close to a plain method call; JSON and
  binary services pay the request/response machinery (admission, result objects, a `ValueTask`
  state machine when the handler completes asynchronously). The exact ratios are in the services
  table; `Reject` adds nothing to calls that carry closed values.
- JSON serialization dominates JSON calls, not the runtime: each side (the handler through
  `PluginOperations`, the caller through the SDK `InvokeAsync<TRequest, TResponse>`) pays one
  `FromPayload` plus one `ToPayload`, about 550 ns together, which is also the difference between
  the raw and the SDK rows; what remains of the raw call is in the range of the binary call.
- No mechanism allocates per call on the provider side beyond what the handler itself allocates;
  the typed proxy allocates nothing for synchronous calls.
- Memory under sustained load: the stream buffers are bounded by construction
  (`MaxStreamCapacity`), binary payloads by `MaxBinaryPayloadBytes`, JSON by `MaxPayloadBytes`; the
  unload tests in `tests/ModularRuntime.Integration.Tests` (`CommunicationUnloadTests`,
  `RepeatedRestarts_LeaveNoRetainedContexts_AndReloadTheContractOnDemand`) check that repeated
  start/stop cycles leave no retained load contexts or contract contexts.
