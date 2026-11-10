# Roadmap

This roadmap records the implemented scope and candidate work for later
versions. Items outside the "Shipped" sections are not implemented.

## Shipped in v0.1

- .NET 10 class library runtime with a small contracts assembly.
- Directory-based plugin discovery with strict `plugin.json` validation.
- One collectible `AssemblyLoadContext` per plugin, with
  `AssemblyDependencyResolver` and shared contracts identity.
- Formal lifecycle: `Discovered`, `Validated`, `Loaded`, `Running`, `Stopping`,
  `Stopped`, `Failed`.
- Per-plugin concurrency gates, cancellation support, ordered start/stop,
  idempotent disposal, and failure isolation.
- Engine/factory boundary that keeps `AssemblyLoadContext` out of the engine.
- `PluginRuntime` facade shared by the standalone host (replaced in v0.3 by the daemon) and embedded sample.
- Interactive host commands:
  `help`, `plugins list`, `plugins info`, `plugins start`, `plugins stop`,
  `plugins start-all`, `plugins stop-all`, `exit`.
- Ctrl+C graceful shutdown in the host.
- Sample hello plugin, intentionally failing plugin, embedded example, and
  plugin staging script.
- Engine, loader, and integration test suites covering real staged assemblies
  and cooperative unload.
- Architecture, plugin development, lifecycle, and security documentation.

## Shipped in v0.1.1 (Stability & Reliability Update)

- Cancellation callbacks never run under the slot state lock; coordinated
  `CancelLifetime` / `DetachInstance` disposal of the lifetime token source.
- Text-only `PluginDiagnostic` in results, log entries, discovery errors, and
  `PluginLoadException` (no inner exceptions from plugins).
- Defensive diagnostic capture for exceptions whose members throw.
- Best-effort logging: host logger failures cannot corrupt lifecycle state.
- Engine disposal cancels plugins that are still initializing.
- Reliability and `WeakReference` unload test suites.

See [V0.1.1_STABILITY.md](V0.1.1_STABILITY.md).

## Shipped in v0.2.0 (Extensibility Platform)

- Experimental plugin API `0.2` (contracts `AssemblyVersion` `0.2.0.0`); no
  compatibility layer for API 1.0 (the project is unreleased).
- `IRuntimeScope` shared by plugins (`IPluginContext`) and hosts
  (`PluginRuntime.Host`).
- Event bus: ordered asynchronous dispatch, JSON payloads with size limits,
  failure isolation, reentrancy with a depth limit, per-instance subscriptions.
- Service registry: one provider per ID, versioned discovery, structured
  invocation results, identifier-only client handles.
- Extension points declared by hosts or plugins, versioned contributions,
  single and aggregated invocation.
- Resource ownership: per-instance scopes, automatic cleanup on stop, failed
  start, and disposal; drain of in-flight handlers before `ShutdownAsync`;
  reentrant stop/dispose rejection; `startOrder` in manifests.
- Standalone CLI commands for events, services, extensions, and resources;
  embedded example covering the whole platform; six new sample plugins.
- Real load-context tests for handler release, retained handles, retained
  snapshots, failed initialization, in-flight handlers, retained results, plus a
  negative control.

See [V0.2_EXTENSIBILITY.md](V0.2_EXTENSIBILITY.md).

## Shipped in v0.2.1 (concurrency fixes)

- Resource release is one shared operation: concurrent `DisposeAsync`, scope close, and
  extension point removal all observe the same completion; unload is never requested while
  resource cleanup is pending.
- Scope close waits for cancellation callbacks started on another thread; `Cancel()` and
  `Dispose()` of the scope token source never overlap; concurrent closes share one operation.

See [V0.2.1_CONCURRENCY.md](V0.2.1_CONCURRENCY.md).

## Shipped in v0.3.0 (Independent Runtime & WPF Desktop)

- `ModularRuntime.Daemon`: background process that owns the runtime; one
  instance per user profile (mutex + first pipe instance); explicit shutdown
  bounded by a timeout; exit codes; daily log files; `config/daemon.json`.
- IPC protocol v1 over a per-user named pipe: framing with size limits, exact
  version handshake, correlated and cancellable requests, notifications,
  structured errors, backpressure with drop counting; current-user-only ACL,
  network logons denied.
- Engine state version with `WaitForStateChangeAsync`; incremental
  `state.changed` notifications plus snapshots.
- Log store with sequence numbers, history queries, filtered live subscriptions,
  and gap detection.
- `ModularRuntime.Cli`: status, daemon start/stop, plugin lifecycle, catalogs,
  logs (with `--follow`), JSON output, exit codes. Replaces the interactive
  `ModularRuntime.Host`.
- `ModularRuntime.Desktop`: WPF app (MVVM, dark/light theme, custom chrome,
  per-monitor DPI) with dashboard, plugins, events, services, extensions, live
  logs, and settings; connection supervision with reconnection; closing it never
  stops the daemon.
- Detached daemon launch (`CreateProcess` without handle inheritance, job
  breakaway when allowed) shared by the CLI and the desktop app.
- `scripts/publish.ps1` producing the three executables in one folder.
- IPC, daemon (including real-process independence scenarios), and desktop view
  model test suites.

See [V0.3_DESKTOP.md](V0.3_DESKTOP.md).

## Shipped in v0.4.0 (Secure Plugin Manager & Official Registry)

- Package format with signed `integrity.json` (SHA-256 and size of every file), strict path
  rules, archive limits enforced on real decompressed bytes.
- ECDSA P-256 signatures (DER), key IDs, purposes, revocation, test-key separation; official
  public keys compiled in (no production key yet).
- Authenticated official registry: signed catalog, strict schema, monotonic revision against
  rollback, expiry, offline cache, size-limited downloads, https or local mirrors.
- Daemon package manager: install official, install local ZIP (uploaded to staging), verify,
  update, remove, check updates, background operations with progress and cancellation,
  journaled atomic installation with crash recovery, verification before every load with file
  locking, unsigned packages never auto-started.
- Engine `UnregisterAsync`; Loader verification hook and dynamic registration.
- Desktop Plugins page with Installed, Explore, Updates, Install from ZIP, trust states.
- CLI `registry …` and `plugins install | install-zip | verify | update | remove | updates | packages`.
- The administrator's publisher (prepare, approval-checked sign, pack, verify, registry
  build/sign/verify), the demo catalog script, and the registry repository template (maintained
  privately since the publishing split).

See [V0.4_PLUGIN_MANAGER.md](V0.4_PLUGIN_MANAGER.md).

## Shipped in v0.5.0 (Developer SDK & Plugin Templates)

- Product version 0.5.0 everywhere from one property; plugin API 0.3 (`IPluginContext.Descriptor`)
  managed separately ([VERSIONING.md](VERSIONING.md)).
- Unload tracking fixed: live contexts stay tracked after timeouts and cancellations, records of
  collected contexts are pruned, collection is induced only on request, and the System.Text.Json
  reflection caches that retained plugin types are flushed when it is.
- `ModularRuntime.Sdk`, the `modularruntime-plugin` template (six kinds), and the
  `dotnet modularruntime` tool (validate, pack, inspect, prepare-submission, doctor).
- Local NuGet packages for Contracts, Sdk, Engine, Loader, Templates, and DevTools, tested from
  outside the repository against the real daemon and an embedded host.

See [V0.5_DEVELOPER_SDK.md](V0.5_DEVELOPER_SDK.md).

## Shipped in v0.6.0 (Official Publishing Infrastructure)

- Demo catalog regenerated for plugin API 0.3 by the script itself (staged, verified, immutable
  versions, increasing revisions), with a regression guard in `registry-build`.
- Submission format v2 and static validator; auditable case state machine; review records;
  controlled builds with build records; signed approvals bound to the exact build output.
- Administrator tools: key purposes and separation, candidate preparation, approval-bound
  signing, provenance, release verification, withdrawal/revocation (maintained in a private
  repository since the publishing split; the catalog rules became public `registry-check`).
- Registry schema 2 (status, provenance, revoked keys); daemon revocation gate (offline too);
  Store and Plugins page show status, provenance, history, and revocations.
- Hardened registry repository template with four workflows (not activated).

See [V0.6_OFFICIAL_PUBLISHING.md](V0.6_OFFICIAL_PUBLISHING.md).

## Candidate work

### Plugin manager (after v0.4)

- Production signing key ceremony and the official registry location.
- Signing through a CNG/HSM key provider; separate package and registry keys.
- Online revocation list or short-lived signed timestamps.
- Verification of unmanaged plugins (opt-in), plugin dependency declarations.
- Process isolation for untrusted plugins (out-of-process workers).

### Desktop and daemon (after v0.3)

- Optional start at logon (per-user, opt-in) and an optional Windows Service
  host for machine-wide deployments.
- Enabling and disabling plugins persistently (installing, removing, and restarting
  are available — see [STORE_AND_RESTART.md](STORE_AND_RESTART.md)).
- Event publishing and service invocation from clients behind an explicit
  permission model.
- Notifications for plugin failures (tray icon, toasts).
- Snap layouts on the custom maximize button; UI automation tests.

### Platform

- Per-operation timeouts for lifecycle methods, invocations, and resource cleanup
  with an explicit policy (the v0.2 drain timeout is the only bounded wait; since
  v0.2.1 a cleanup that never completes blocks its owner's teardown).
- Optional parallel or queued event dispatch per subscriber, with bounded queues.
- Topic patterns (prefix or wildcard subscriptions).
- Optional JSON schema metadata for services, topics, and extension points.
- Declared plugin dependencies (`requires` service/point) to derive start order
  instead of manual `startOrder` values.
- Diagnostics API: in-flight invocation counts, drain timeouts, and unload status
  per plugin.

### Reliability and operations

- Health checks and readiness reporting for embedded hosts.
- Start/stop timeouts with explicit policy and diagnostics.
- Structured logging adapter packages and correlation IDs across operations.
- Diagnostic snapshots including load-context state and unload status.
- Windows service and generic-host integration samples.
- Graceful shutdown hardening for blocked or non-cooperative plugins.

### Plugin lifecycle

- Watched plugin directories and atomic replacement of stopped installations.
- Optional graceful drain phase before `ShutdownAsync`.
- Plugin dependency/compatibility metadata beyond the current API version check.
- Per-plugin configuration sections supplied by the host.

### Packaging and supply chain

- A documented plugin package format with deterministic layout.
- Optional publisher metadata and signature/hash verification.
- A local plugin catalog or feed, with explicit trust configuration.
- Template project and `dotnet new` template for plugin authors.
- Automated validation tooling for manifests and staged outputs.

### Isolation

- Optional out-of-process plugin host for crash/resource isolation.
- OS-level sandbox guidance and hardened deployment profiles.
- Resource accounting for plugins that opt into limits.
- Cross-platform verification (Linux and macOS) if those targets are adopted.

### Developer experience

- More sample plugins covering dependency injection, background services, and
  native dependencies.
- Create the registry repository, hold the key ceremony, configure the official catalog URL,
  and activate the workflows.
- Sign through an HSM or smart card (CNG key provider) instead of PEM files.
- Two-approver rule for high-risk plugins (several approval signatures per release).
- Signed transparency log of catalog revisions and approvals.
- Reproducible-build comparison of two independent controlled builds.
- Analyzer package for plugin authors (diagnostics in the IDE for the rules `validate` checks).
- Publish the SDK packages to a public feed once a license and repository are defined.
- A `submit` command that uploads a prepared submission to the administrator's intake service.
- Source-generated JSON contexts in the templates (avoids reflection caches entirely).
- Signed DevTools and template packages (NuGet package signing).
- Public API compatibility tests across contracts versions.
- Benchmark suite for discovery, load, start, stop, and unload.
- CI configuration for Debug/Release builds and tests.

## Non-goals (current)

- In-process sandboxing or a permission system.
- Native AOT support for the host or engine.
- Hot reload of a running plugin instance.
- Loading plugins over HTTP or from arbitrary assemblies.
- Automatic download or update of plugins.
- Guaranteed immediate unload of collectible load contexts.
- Supporting every .NET target framework in a single plugin directory.
- Distributed or persistent event bus, remote plugins, or out-of-process plugin
  workers (since v0.3, IPC connects only the daemon and its local clients; all
  plugins of a profile run inside the daemon).
- Arbitrary dependency injection between plugins.

## Compatibility policy

While ModularRuntime is pre-release the plugin API is versioned 0.x: every 0.x
minor may contain breaking changes, the manifest must declare exactly the
supported `major.minor`, and the contracts assembly version follows the API
version (`0.2.0.0`). Plugins are rebuilt for each 0.x minor; no compatibility
layers are kept.

From API 1.0 onward, `ModularRuntime.Contracts` will keep a fixed assembly
identity for each major API line. Additive changes increment the supported minor
version; breaking changes require a major version bump. The runtime and plugin
manifest must agree on a compatible version before a plugin can be registered.
