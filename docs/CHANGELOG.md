# Changelog

One entry per release, newest first. The documentation describes the current product; this file
keeps what each version changed and the compatibility notes that still matter.

## 0.7.0 — Communication platform (plugin API 0.4)

**Plugin API 0.3 → 0.4** (contracts `AssemblyVersion` 0.4.0.0). Plugins built for 0.3 must be
rebuilt against the 0.7.0 packages and declare `"apiVersion": "0.4"`; no source change is needed.

- **Typed services**: `context.TypedServices.Register<TContract>(definition, instance)` and
  `Resolve<TContract>(id)` / `GetClient<TContract>(id)`. Consumers get a runtime-generated proxy
  (direct calls, no serialization) bound to one registration, admitted through the provider's
  scope, canceled on provider shutdown, with exception translation and the shared-value check.
  Contracts are shared through **shared contract assemblies** declared in `plugin.json`
  (`sharedContracts`), loaded once per version line into a collectible shared context by the
  loader and unloaded when the last user stops ([COMMUNICATION.md](COMMUNICATION.md#typed-services)).
- **Shared values, enforced by construction** ([COMMUNICATION.md](COMMUNICATION.md#shared-values)):
  every value crossing a typed boundary (typed-service and callback arguments and results, stream
  items) must reference shared types only. Types are classified once as closed (never
  inspected), open (the instance is inspected by a generated, bounded, iterative plan: fields,
  collection entries and comparers, delegate targets, exception chains, reflection objects,
  memory managers) or private (refused). **`RuntimeLimits.TypedValueCheck` defaults to
  `Reject`**: offenders fail with `value_not_shared`, values that cannot be verified within
  `MaxValueScanReferences` (4096) / `MaxValueScanDepth` (64) with `value_not_verifiable`, and
  contract signatures are validated at registration (`typed_service_invalid_contract`). `Warn`
  (the previous default) and `None` remain available for migration and trusted hosts.
- **Callbacks**: `context.TypedServices.RegisterCallback<TDelegate>(name, target)` returns an
  owned registration (`ResourceKind.Callback`) whose `Callback` is a shared delegate that
  forwards to the target through the owner's scope and holds nothing of the owner; invoking it
  after release answers `callback_released` (`callback_unavailable` while the owner shuts down).
  Raw delegates over plugin code are refused at the boundary by default.
- **Binary services**: `context.Services.RegisterBinary` / `InvokeBinaryAsync` /
  `IPluginServiceClient.InvokeBinaryAsync` with explicit buffer ownership (borrowed requests,
  transferred responses, pooled responses disposed by the caller) and per-service byte limits.
- **Streams**: `context.Streams.Create<T>` / `Subscribe<T>` with bounded per-subscriber buffers,
  `Wait` (backpressure) or drop policies with loss accounting and sequence numbers, completion
  reasons, and copy-on-write binary items.
- **JSON**: `JsonPayload` stores UTF-8 (`Utf8Json`, `Parse(ReadOnlySpan<byte>)`, `WriteTo`),
  validates with a forward-only reader (no intermediate document), reads `GetString` without
  building a document, and decodes `Json` lazily. Behavior and limits are unchanged.
- One service ID namespace with `PluginServiceDescriptor.Kind` (`Json`, `Binary`, `Typed`) and
  `ContractTypeName`; new `ResourceKind.Stream`, `StreamSubscription`, and `Callback`; new error
  codes (`service_kind_mismatch`, `value_not_shared`, `value_not_verifiable`, `callback_*`,
  `typed_service_*`, `stream_*`, `shared_contract_*`).
- Engine: `RuntimeLimits.MaxBinaryPayloadBytes`, `DefaultStreamCapacity`, `MaxStreamCapacity`,
  `TypedValueCheck` (default `Reject`), `MaxValueScanReferences`, `MaxValueScanDepth`;
  `PluginEngine(logger, limits, ISharedTypePolicy)` with `ISharedTypePolicy.IsSharedAssembly`;
  synchronous typed calls use a thread-local reentrancy frame (no allocation); closed arguments
  and results are not checked at all, open ones cost one weak-table lookup when their runtime
  type is closed; a `CancellationToken` parameter is not checked because the callee receives a
  token owned by the call (linked to the caller's token and the callee's scope, detached when the
  call ends, so a kept token retains nothing of the caller); an exception that carries a private
  field, inner exception, or `Data` entry becomes a `TypedServiceException`.
- Loader: `sharedContracts` in the manifest (`PluginDescriptor.SharedContracts`),
  `SharedContractRules`, `PluginLoader.GetSharedContracts()` / `PluginRuntime.GetSharedContracts()`,
  load error codes `shared_contract_missing`, `shared_contract_invalid`,
  `shared_contract_version_conflict`, `shared_contract_dependency`. A shared contract that
  references another shared contract binds to the loaded copy of the major it references.
- IPC protocol 1 (additive): `services.list` entries carry `kind` and `contractType`;
  `streams.list` returns streams and loaded shared contracts; `runtime.status` carries
  `streamCount`. CLI `streams list`; desktop Streams page and a Kind column on Services. A
  handshake that is not answered within the client's timeout is a `TimeoutException` (it
  surfaced as a cancellation), so `DaemonLauncher.IsRunningAsync` reports "not running" and
  `EnsureRunningAsync` keeps polling while a daemon is still starting on a busy machine.
- SDK: `BinaryOperations`, `RegisterBinary(id, name, version, operations)`,
  `context.Consume<T>` / `ConsumeJson<T>`, `WriteJsonAsync<T>`; `PluginJson` serializes to UTF-8.
- Templates: kinds `typed-provider`, `typed-consumer` (with a `Contracts/` shared contract
  project and `--contract-assembly`), `binary-provider`, `stream-publisher`, `stream-subscriber`
  (`--stream-id`). DevTools `validate` checks shared contracts (`shared_contract_dependency`,
  `shared_contract_unused`).
- Samples: `Example.Telemetry.Contracts`, `TypedProviderPlugin`, `TypedConsumerPlugin`,
  `BinaryProviderPlugin`, `StreamPublisherPlugin`, `StreamSubscriberPlugin`.
- Benchmarks (`benchmarks/ModularRuntime.Benchmarks`, BenchmarkDotNet) and
  [BENCHMARKS.md](BENCHMARKS.md).
- Documentation consolidated into thematic guides; the per-version documents were folded into
  this changelog.

## 0.6.0 — Official publishing infrastructure

Plugin API unchanged (0.3).

- Submission format v2 (`submission.json`, `source-integrity.json`: license, source repository
  and commit, lock files) and the public static validator pieces (`ModularRuntime.Submissions`,
  secret classifier); `prepare-submission --license --source-repository --source-commit`.
- Registry catalog schema 2: per-version `status` (published / withdrawn / revoked),
  `statusReason`, `statusChangedAt`, `advisoryUrl`, `buildId`, `approvalSha256`; top-level
  `revokedKeys`. `RegistryCatalogCheck` and `dotnet modularruntime registry-check` implement the
  public publication rules (signature, validity, rollback, immutable versions, kept revocations).
- Daemon `RevocationGate` before the integrity check (offline too); `Revoked` trust state;
  running plugins are warned, never loaded again after they stop.
- Key purpose `approvals` (never trusted by the daemon); approval records bound to a controlled
  build; the administrator's review, build, approval, and signing tools moved to a private
  repository that depends on this one.
- Template: `RestorePackagesWithLockFile`.
- v0.4/v0.5 daemons reject schema 2 catalogs (fail-safe).

## 0.5.0 — Developer SDK and plugin templates

**Plugin API 0.2 → 0.3**: `IPluginContext.Descriptor` (the validated manifest). Plugins declare
`"apiVersion": "0.3"` and are rebuilt.

- Product version in one property (`VersionPrefix`), plugin API version separate
  ([VERSIONING.md](VERSIONING.md)); the previous "0.4.1" had kept `VersionPrefix` 0.4.0.
- `ModularRuntime.Sdk` (`PluginJson`, `PluginOperations`, typed event/service/extension
  helpers, `RunInBackground`), `ModularRuntime.Templates` (`dotnet new modularruntime-plugin`,
  six kinds), `ModularRuntime.DevTools` (`dotnet modularruntime validate · pack · inspect ·
  prepare-submission · doctor`), all reusing the Loader's manifest rules, the Packaging builder and
  verifier, and metadata-only assembly inspection. `scripts/pack-sdk.ps1` packs six local packages.
- Unload tracking fixed: `RetiredLoadContextTracker` keeps live contexts tracked after timeouts
  and cancellations, prunes collected ones, forces nothing unless `PluginUnloadWait.InduceCollection`
  is requested, and then first flushes the System.Text.Json reflection caches that retained
  plugin types; `GetPendingUnloadCount`.
- End-to-end tests from outside the repository with the packed SDK (template kinds, tool, real
  daemon, embedded host, unload).

## 0.4.0 — Secure plugin manager and official registry

- Package format: ZIP with signed `integrity.json` (SHA-256 and size of every file), strict path
  rules, archive limits enforced on real decompressed bytes
  ([PLUGIN_PACKAGE_FORMAT.md](PLUGIN_PACKAGE_FORMAT.md)).
- ECDSA P-256 signatures, key IDs, purposes, revocation, test-key separation
  ([PLUGIN_SIGNING.md](PLUGIN_SIGNING.md)); official public keys compiled in.
- Authenticated registry: signed catalog, strict schema, monotonic revision against rollback,
  expiry, offline cache, size-limited downloads ([OFFICIAL_REGISTRY.md](OFFICIAL_REGISTRY.md)).
- Daemon package manager: install official, install local ZIP (uploaded in chunks to staging),
  verify, update, remove, check updates; background operations with progress and cancellation;
  journaled atomic installation with crash recovery; verification before every load with file
  locking; unsigned packages never auto-started.
- Engine `UnregisterAsync`; Loader `IPluginLoadVerifier`, `RegisterInstallationAsync`,
  `UnregisterAsync`, `WaitForUnloadAsync`; `author` and `description` in `plugin.json`.
- Desktop Plugins page (later split into Store and Plugins), CLI `registry …` and
  `plugins install | install-zip | verify | update | remove | updates | packages`.
- Later in the 0.4 line: a stopped or failed plugin can be started again in the same daemon
  (`PluginRuntime` renews the registration after re-reading and re-verifying the installation);
  Store page separated from the Plugins page.

## 0.3.0 — Independent runtime and WPF desktop

Plugin API unchanged (0.2).

- `ModularRuntime.Daemon`: one background process per user profile owns the runtime (mutex plus
  first pipe instance), explicit shutdown bounded by a timeout, exit codes, daily log files,
  `config/daemon.json`.
- IPC protocol 1 over a per-user named pipe: framing with size limits, exact version handshake,
  correlated and cancellable requests, notifications, structured errors, backpressure with drop
  counting; current-user-only ACL, network logons denied ([IPC_PROTOCOL.md](IPC_PROTOCOL.md)).
- Engine state version with `WaitForStateChangeAsync`; `state.changed` notifications.
- Log store with sequence numbers, history queries, filtered live subscriptions, gap detection.
- `ModularRuntime.Cli` (replaces the interactive console host) and `ModularRuntime.Desktop` (WPF,
  MVVM, themes, connection supervision with reconnection; closing it never stops the daemon).
- Detached daemon launch without handle inheritance (a daemon started by the CLI used to keep
  the CLI's redirected output open); elevated clients can reach a normal daemon (the client
  verifies the pipe owner itself instead of `PipeOptions.CurrentUserOnly`).

## 0.2.1 — Concurrency fixes

- Resource release is one shared operation: concurrent `DisposeAsync`, scope close, and
  extension point removal observe the same completion; a resource leaves its scope only after
  its release finished, so unload is never requested while cleanup is pending.
- Scope close waits for cancellation callbacks started on another thread; `Cancel()` and
  `Dispose()` of the scope token source never overlap; concurrent closes share one operation.
- Trade-off kept since: a resource cleanup or cancellation callback that never completes blocks
  its owner's teardown; there is no cleanup timeout.

## 0.2.0 — Extensibility platform

**Plugin API 1.0 → 0.2** (experimental 0.x line; contracts `AssemblyVersion` 0.2.0.0; no
compatibility layer for 0.1 plugins).

- `IRuntimeScope` shared by plugins (`IPluginContext`) and hosts (`PluginRuntime.Host`).
- Event bus (ordered asynchronous dispatch, JSON payloads with size limits, failure isolation,
  bounded nested publication), service registry (one provider per ID, versioned discovery,
  structured results, identifier-only client handles), extension points (declared by hosts or
  plugins, versioned contributions, single and aggregated invocation).
- Resource ownership: per-instance scopes, automatic cleanup on stop, failed start, and disposal;
  drain of in-flight handlers before `ShutdownAsync`; reentrant stop/dispose rejection;
  `startOrder` in manifests.
- Real load-context tests for handler release, retained handles, retained snapshots, failed
  initialization, in-flight handlers, retained results, plus a negative control. Two hidden
  roots were found and fixed: locals hoisted into async state machines in Debug builds, and
  continuations running on top of plugin frames (`ThreadPoolHop`).

## 0.1.1 — Stability and reliability

Plugin API 1.0 unchanged.

- Cancellation callbacks never run under the slot lock; coordinated disposal of the lifetime
  token source.
- Text-only `PluginDiagnostic` in results, log entries, discovery errors, and `PluginLoadException`
  (hosts that read exception objects must use `Diagnostic`: source-breaking for hosts).
- Defensive diagnostic capture (members that throw are replaced by placeholders); best-effort
  logging (a failing host logger never corrupts lifecycle state); disposal cancels plugins that
  are still initializing.
- Reliability and `WeakReference` unload test suites.

## 0.1.0

Initial runtime: .NET 10 class library, strict `plugin.json` discovery, one collectible
`AssemblyLoadContext` per plugin with `AssemblyDependencyResolver` and shared contracts identity,
formal lifecycle (`Discovered`, `Validated`, `Loaded`, `Running`, `Stopping`, `Stopped`, `Failed`),
per-plugin gates, ordered start/stop, idempotent disposal, failure isolation, `PluginRuntime`
facade, interactive host, samples, and test suites.
