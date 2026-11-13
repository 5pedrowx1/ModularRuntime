# Security

## Trust model

**Plugins are trusted code.** ModularRuntime executes plugin code in the host process (the
daemon) with the same operating-system identity and privileges as the host. A plugin can read,
write, or delete files the host can access, read environment variables and process memory, open
network connections, start threads, load native libraries, call any .NET API, and terminate or
hang the process.

`AssemblyLoadContext` is used for dependency and identity isolation. It is **not a sandbox** and
must not be treated as a security boundary. Manifest validation and path checks protect against
malformed installations and accidental path escapes; they do not constrain what a loaded plugin
does after initialization.

### The communication platform

The event bus, JSON/binary/typed services, streams, and extension points are **cooperation
mechanisms between trusted components, not security boundaries**:

- any plugin can subscribe to any topic, publish on any topic, call any service, read or create
  any stream, declare extension points, and contribute to any point;
- the `Source` of an event and the `Caller` of a request identify the owner as recorded by the
  runtime, which is reliable only because all plugins are trusted in-process code;
- there are no permissions, quotas, or rate limits per plugin; first registration of a service,
  stream, or extension point ID wins; hosts that depend on a specific provider must check
  `PluginServiceDescriptor.Owner`;
- handlers and typed service methods run in-process and cannot be interrupted if they ignore
  cancellation; a typed service method runs on the caller's thread.

**Typed services and shared contract assemblies** add one more trusted dependency: a contract
assembly declared by several plugins is loaded once, from the bytes of the first plugin that
needs it (after that plugin's installation was verified), and executed by every plugin that uses
it. A contract may reference only the framework, `ModularRuntime.Contracts`, and other declared
shared contracts; the loader refuses anything else (`shared_contract_dependency`), so a plugin's
private assemblies never leak into others. The runtime's proxies keep provider objects out of
consumers' hands, callbacks cross as runtime-owned wrappers, and the shared-value check
(`RuntimeLimits.TypedValueCheck`, `Reject` by default) refuses plugin-private objects crossing a
boundary, including ones hidden inside shared objects, collections, delegates, or exceptions, and
refuses values it cannot verify within its limits; all of this serves lifetime hygiene, not
security.
Nothing is ever loaded into the default load context on a plugin's behalf.

## What the runtime validates

- Plugin root entries are immediate subdirectories containing `plugin.json`; manifests are
  limited to 256 KiB, parsed as strict JSON, and reject unknown fields; IDs, names, versions, API
  versions, entry types, paths, and shared contract names are validated before registration.
- The entry assembly path must be relative, end in `.dll`, and remain inside the plugin directory
  (`Path.GetFullPath`); the file must exist and reference the shared contracts assembly with a
  compatible version; the entry type must be public, concrete, non-generic, implement `IPlugin`,
  and have a public parameterless constructor. Duplicate plugin IDs are rejected after the first
  valid manifest. API compatibility is checked at discovery, registration, and load.
- Shared contract files must exist next to the entry assembly, carry the declared assembly name,
  satisfy the plugin's referenced version, and follow the dependency rule above.
- Platform identifiers are runtime names (lowercase ASCII, digits, `.`, `_`, `-`, ≤ 128
  characters). JSON payloads are validated JSON (no comments, maximum depth 64) limited to
  `RuntimeLimits.MaxPayloadBytes` (256 KiB); binary payloads to `MaxBinaryPayloadBytes` (16 MiB,
  lowerable per service); metadata to 32 entries (keys ≤ 64, values ≤ 1024 characters); stream
  buffers to `MaxStreamCapacity`; nested publications to `MaxEventDispatchDepth`. Payloads are
  never executed or deserialized into CLR types by the runtime; there is no `BinaryFormatter` or
  type-name-based deserialization; typed service contracts are validated (public interface, no
  static abstract members) and proxied, never deserialized.

## What the runtime does not provide

No sandbox, permission, capability, or per-plugin user; no network, filesystem, registry, or
process isolation; no resource quotas, CPU or memory limits, or execution timeouts; no protection
against a plugin that blocks a thread indefinitely or calls `Environment.Exit`; no isolation from
process-wide static state in the default load context; no secret store; no audit trail beyond the
configured `IRuntimeLogger`.

Deployment guidance: run the daemon under a dedicated, least-privileged account; restrict write
access to the plugin root (whoever can replace plugin files executes code as the user); treat
manifests, assemblies, dependencies, shared contracts, and native libraries as executable
supply-chain artifacts; keep private dependencies inside the plugin directory; use OS-level
isolation (container, VM, restricted account, job objects) for code you do not fully trust; avoid
secrets in the daemon's environment when untrusted plugins run; monitor logs and process health.

## IPC security

The runtime is controlled through a named pipe served by `ModularRuntime.Daemon`
([IPC_PROTOCOL.md](IPC_PROTOCOL.md)). The trust boundary is the **Windows user account**.

- **Local only.** Named pipes only; no TCP or HTTP listener. The pipe DACL has an explicit deny
  entry for the `NETWORK` SID, so network logons are refused even for the same account.
- **Current user only.** The DACL is protected (no inherited entries) and grants access to exactly
  one SID: the user running the daemon; the pipe owner is always the user SID, also when the
  daemon runs elevated.
- **Server authentication by the client.** Before sending anything, the client reads the pipe's
  owner and refuses the connection unless it is the current user's SID (or the token's default
  owner, for pre-0.3.0 elevated daemons). `PipeOptions.CurrentUserOnly` is deliberately not used:
  it compares with the token's default owner, which is `Administrators` in an elevated process.
- **No squatting.** The daemon creates its first pipe instance with `FirstPipeInstance`; if any
  process already owns that name, the daemon refuses to start (exit code 2). The pipe name embeds
  a hash of the user SID and the profile path; the single-instance mutex uses the same key.
- **Closed operation set.** Only the documented operations exist: status, shutdown, plugin
  lifecycle by ID, catalog snapshots (events, services, streams, extensions), logs, registry and
  package operations. No operation executes code, evaluates expressions, reads or writes
  arbitrary files, loads assemblies, publishes events, invokes services, or writes streams.
  Plugin folders come from the daemon's configuration, never from a client; uploaded ZIPs go to
  daemon-owned staging and the daemon never reads a client-supplied file path.
- **Validated input, bounded resources.** Payloads are deserialized into fixed DTOs; identifiers
  are length-limited and validated; message size (1 MiB default), handshake timeout, connections,
  requests in flight, notification queues, and log subscriptions are bounded; a misbehaving client
  affects only its own connection.
- **No implicit stop.** Closing a client never stops the daemon; shutdown is an explicit,
  confirmed operation.

Limitations: any process running as the same user (including malware running as that user) can
connect and administer that user's daemon, including stopping it and reading its logs: the same
boundary as the user's own files. An elevated and a non-elevated process of the same user are the
same principal for the DACL (verified: an elevated CLI controls a non-elevated daemon; a
non-elevated client against an elevated daemon was not verified). Connection attempts from another
account or a remote machine were not exercised by tests (single-account test machine). Logs and
diagnostics served over IPC may contain paths and stack traces.

## Plugin packages and the official registry

The package manager adds authenticity and integrity, not isolation.

- **A digital signature** establishes that the package (its signed file list, and through it
  every file) was produced by the holder of a trusted key and has not changed since. It does
  **not** make code safe. **Code review and controlled builds** reduce the risk that an official
  package is malicious or vulnerable; they do not eliminate it. **SHA-256 checks** detect changes
  relative to authenticated hashes; hashes from the same untrusted source as the files (an
  unsigned package's own `integrity.json`) only show internal consistency.
- Official packages and the registry are signed with ECDSA P-256 keys held only by the
  administrator; the daemon ships public keys only and accepts no keys from downloaded content,
  configuration files, or clients ([PLUGIN_SIGNING.md](PLUGIN_SIGNING.md)). Registry, package,
  and approval keys are separate with one purpose each; the `approvals` purpose is never trusted
  by the daemon.
- Every installation is verified completely in daemon-owned staging (package hash from the
  signed registry, signature, SHA-256 and size of every file, safe paths, limits) before it is
  published atomically; no plugin code runs during verification
  ([PLUGIN_PACKAGE_FORMAT.md](PLUGIN_PACKAGE_FORMAT.md)).
- The registry is protected against rollback by a monotonic revision and against indefinite
  replay by an expiry date; a network attacker can replay an older unexpired catalog to a fresh
  or offline profile but cannot forge entries or packages ([OFFICIAL_REGISTRY.md](OFFICIAL_REGISTRY.md)).
- Unsigned local packages require explicit acceptance, are always labelled "Local, unverified",
  and are never started automatically. `--test-trust-store` (development only) is never read from
  configuration, and its use is logged and visible in every client.
- Withdrawals and revocations are signed catalog changes applied before every load, offline too;
  the daemon never trusts an unsigned revocation ([PUBLISHING.md](PUBLISHING.md#withdrawal-and-revocation)).

### Verification before load and the race window

Before a managed plugin's assemblies are loaded (every start), the loader calls the daemon's
`PackageLoadVerifier`:

1. the recorded `integrity.json` and, for official packages, `integrity.sig` are re-verified with
   the **current** trust store (a key revoked since installation stops the load), after the
   `RevocationGate` refused revoked versions and keys;
2. plugin ID and version are compared with `plugin.json` and the record;
3. the installation directory is enumerated: no reparse points or symbolic links, no extra files,
   no missing files;
4. each file is opened **for reading with read sharing only** and hashed through that handle.

The handles stay open while the plugin is loaded (released when it stops), so no process can
write, replace, rename, or delete those files meanwhile: the bytes that were verified are the
bytes the runtime loads, including shared contract assemblies, which are read through the same
lease and loaded from memory. Limits of this protection: a process running as the same user can
still kill or debug the daemon, inject into it, edit the daemon's own executables, change the
profile's records or trust state while the daemon is stopped, or start it with
`--test-trust-store`; after a plugin stops, its assemblies may stay mapped until the load context
is collected, so updates and removals wait for unload (`files_in_use` otherwise); unmanaged
(hand-copied) plugins are loaded without verification; a plugin that writes into its own
installation directory is reported as modified at the next verification.

Daemon authority: clients cannot write installation directories, mark anything verified, bypass
signature errors, replace files of running plugins (updates and removals stop the plugin first,
with an explicit `stopIfRunning`), modify trusted keys, sign packages, or run arbitrary
administrative actions.

## Developer tool and publishing

- `dotnet modularruntime pack` produces **unsigned** local packages; the tool contains no signing
  code and no keys. `validate` and `inspect` never execute plugin code (metadata only; packages
  are verified by the daemon's `PackageVerifier` into a temporary directory); builds happen only
  through `dotnet build` when requested, which runs the project's MSBuild logic: build only
  projects you trust. `inspect` rejects a signature by an unknown key and never shows it as
  "unsigned". `prepare-submission` excludes build output, IDE state, keys, certificates, credential
  files, files containing PEM private keys, and NuGet configurations with credentials, and does
  not follow symbolic links.
- Official signatures are produced only on the offline signing machine, only for the exact
  `integrity.json` of an accepted controlled build, and only under a signed approval. Submitted
  code is validated statically (never executed) and built only in a controlled build with a
  minimal environment, allowed package sources, locked restore, and isolation; the build refuses
  to run where signing material is configured. The registry repository uses read-only workflow
  tokens, no secrets, actions pinned by commit SHA, and no `pull_request_target`; its CI verifies
  catalogs with the public `registry-check` and signs or publishes nothing
  ([PUBLISHING.md](PUBLISHING.md)).

## Path handling

The entry assembly path is validated lexically: rooted paths and drive/colon syntax are rejected;
leading/trailing whitespace and NUL characters are rejected; the resolved path must not escape the
plugin directory; the file must have a `.dll` extension and exist. Shared contract names cannot
contain path separators or an extension. The check does not resolve symbolic links or junctions
(the package manager's verification does refuse reparse points inside managed installations); use
filesystem ACLs to prevent untrusted users from creating links in the plugin root.

## Error and log exposure

Plugin exceptions, paths, and messages may be included in operation results, event delivery and
invocation results returned to callers that may be *other plugins* (including `PluginDiagnostic`
text of handler exceptions and of exceptions translated by typed proxies), `PluginRuntimeInfo.LastError`,
and `IRuntimeLogger` entries. The engine never retains exception objects, but the text can still
contain sensitive information supplied by a plugin or by the environment, including full stack
traces and file-system paths of the build machine and of the installation. The daemon writes
these details to its log file and serves them to IPC clients of the same user; the CLI and the
desktop app display them.

## Dependency identity

`ModularRuntime.Contracts` is always resolved from the runtime's default load context, never from
a plugin-local copy, and the loader checks the entry assembly's contracts reference version. Shared
contract assemblies are resolved from their shared context for every plugin that declares them. A
plugin can still load additional assemblies explicitly; the runtime cannot prevent that.

## Reporting security issues

ModularRuntime (pre-release) does not define a formal security-reporting process. Until one is
added, treat security-sensitive deployments as trusted-code deployments and apply the controls
above.
