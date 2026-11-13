# Roadmap

What exists is described by the documentation and summarized per version in
[CHANGELOG.md](CHANGELOG.md). This file lists candidate work and non-goals. Items here are not
implemented.

## Candidate work

### Plugin dependencies (proposed as a separate evolution)

Today plugins discover services, streams, and extension points by ID at run time, and
`startOrder` is set by hand. Infrastructure plugins (for example a future `ModularRuntime.ImGui`
providing services to other plugins) would benefit from declarative dependencies:

- manifest `requires` / `optional` entries naming service, stream, or extension point IDs with a
  minimum version, validated at discovery against the plugins present;
- start order derived from the dependency graph (cycles rejected at discovery), with manual
  `startOrder` as a tie-breaker;
- cascading stop (stop dependents before a required provider) and recovery when a provider
  returns (consumers resolve again; availability notifications on reserved event topics such as
  `modularruntime.services.registered`);
- version negotiation for typed contracts beyond "same major, loaded version ≥ referenced":
  for example choosing the newest copy among the plugins present at discovery.

This changes discovery, the engine's start/stop ordering, the IPC DTOs, the Store's compatibility
display, and the registry metadata (declared dependencies per entry), so it is kept separate from
the communication platform. The current behavior for a missing provider is a handled failure
(`service_not_found`, `null` from `Resolve`/`Subscribe`), which the templates demonstrate.

### Platform

- Per-operation timeouts for lifecycle methods, invocations, typed calls, and resource cleanup
  with an explicit policy (the drain timeout is the only bounded wait; a cleanup that never
  completes blocks its owner's teardown).
- Optional parallel event dispatch per subscriber (streams cover the buffered case).
- Topic patterns (prefix or wildcard subscriptions).
- Optional JSON schema metadata for services, topics, streams, and extension points.
- Diagnostics API: in-flight invocation counts, drain timeouts, unload status, stream buffer
  occupancy per plugin.
- Contract-level immutability analysis (flagging mutable DTOs in contract signatures at
  `validate` time) on top of the shared-value check, which already refuses private references.

### Reliability and operations

- Health checks and readiness reporting for embedded hosts; start/stop timeouts with
  diagnostics; graceful shutdown hardening for blocked plugins.
- Structured logging adapter packages and correlation IDs across operations.
- Windows service and generic-host integration samples; optional start at logon.
- Notifications for plugin failures (tray icon, toasts); snap layouts on the custom maximize
  button; UI automation tests.
- Event publishing and service invocation from clients behind an explicit permission model.

### Packaging, supply chain, publishing

- Production signing key ceremony, the official registry location, and activation of the
  registry workflows; signing through a CNG/HSM key provider.
- Online revocation list or short-lived signed timestamps.
- Verification of unmanaged plugins (opt-in); declared dependencies in registry entries.
- Two-approver rule for high-risk plugins; signed transparency log of catalog revisions and
  approvals; reproducible-build comparison of two independent controlled builds.
- Analyzer package for plugin authors (IDE diagnostics for the rules `validate` checks, including
  shared-contract rules); source-generated JSON contexts in the templates; signed DevTools and
  template packages; publication of the SDK packages to a public feed.
- A `submit` command that uploads a prepared submission to the Hub.
- Public API compatibility tests across contracts versions.

### Isolation

- Optional out-of-process plugin host for crash and resource isolation (plugins would then use
  the IPC-style serializable protocol, not typed services).
- OS-level sandbox guidance and hardened deployment profiles; resource accounting for plugins
  that opt into limits.
- Cross-platform verification (Linux and macOS) if those targets are adopted.

## Non-goals (current)

- In-process sandboxing or a permission system.
- Native AOT support for the host or engine (typed proxies use `Reflection.Emit`).
- Hot reload of a running plugin instance.
- Loading plugins over HTTP or from arbitrary assemblies; loading plugin or contract code into
  the default load context.
- Automatic download or update of plugins.
- Guaranteed immediate unload of collectible load contexts.
- Supporting every .NET target framework in a single plugin directory.
- Distributed or persistent event bus, remote plugins, typed services across processes.
- Arbitrary dependency injection between plugins.

## Compatibility policy

While ModularRuntime is pre-release the plugin API is versioned 0.x: every 0.x minor may contain
breaking changes, the manifest must declare exactly the supported `major.minor`, and the contracts
assembly version follows the API version (`0.4.0.0`). Plugins are rebuilt for each 0.x minor; no
compatibility layers are kept. From API 1.0 onward, `ModularRuntime.Contracts` will keep a fixed
assembly identity for each major line; additive changes increment the supported minor, breaking
changes require a major version bump. See [VERSIONING.md](VERSIONING.md).
