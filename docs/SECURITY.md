# Security

## Trust model

**Plugins are trusted code.** ModularRuntime executes plugin code in the host
process with the same operating-system identity and privileges as the host. A
plugin can:

- read, write, or delete files the host can access;
- read environment variables and process memory visible to its identity;
- open network connections;
- start threads and background tasks;
- load native libraries;
- call any .NET API available to the process;
- terminate the process or hang it.

`AssemblyLoadContext` is used for dependency and identity isolation. It is **not
a sandbox** and must not be treated as a security boundary.

The manifest validation and path checks in this repository protect against
malformed installations and accidental path escapes. They do not constrain what
a loaded plugin does after initialization.

### Extensibility platform (v0.2)

The event bus, service registry, and extension points are **cooperation
mechanisms between trusted components, not security boundaries**:

- any plugin can subscribe to any topic, publish on any topic, call any service,
  declare extension points, and contribute to any point;
- the `Source` of an event and the `Caller` of a request identify the owner as
  recorded by the runtime, which is reliable only because all plugins are
  trusted in-process code (a plugin could pass data to another plugin by other
  in-process means);
- there are no permissions, quotas, or rate limits per plugin;
- a plugin can register a service ID or extension point ID before another plugin
  does (first registration wins); hosts that depend on a specific provider must
  check `PluginServiceDescriptor.Owner`;
- handlers run in-process and cannot be interrupted if they ignore cancellation.

No IPC or out-of-process workers exist; isolation of untrusted code would require
process boundaries.

## What the runtime does validate

- Plugin root entries are immediate subdirectories containing `plugin.json`.
- Manifests are limited to 256 KiB, parsed as strict JSON, and reject unknown
  fields.
- Plugin IDs, names, versions, API versions, entry types, and paths are
  validated before registration.
- The entry assembly path must be relative, end in `.dll`, and remain inside the
  plugin installation directory according to `Path.GetFullPath`.
- The entry assembly must exist and reference the shared contracts assembly with
  a compatible major version.
- The entry type must be public, concrete, non-generic, implement `IPlugin`, and
  have a public parameterless constructor.
- Duplicate plugin IDs are rejected after the first valid manifest.
- API compatibility is checked at discovery and registration.
- Platform identifiers (topics, service IDs, extension point IDs, contribution
  IDs, operation names) must be valid runtime names (lowercase ASCII, digits,
  `.`, `_`, `-`, at most 128 characters).
- Payloads are validated JSON (no comments, maximum depth 64) and limited to
  `RuntimeLimits.MaxPayloadBytes` (default 256 KiB) for events, requests, and
  responses. Metadata is limited to 32 entries (keys ≤ 64, values ≤ 1024
  characters). Payloads are never executed or deserialized into CLR types by the
  runtime; there is no `BinaryFormatter` or type-name-based deserialization.
- Nested event publication is bounded by `RuntimeLimits.MaxEventDispatchDepth`.

## What the runtime does not provide

- no sandbox, permission model, capability model, or per-plugin user;
- no code signing or manifest signature verification;
- no hash or publisher verification;
- no network, filesystem, registry, or process isolation;
- no resource quotas, CPU limits, memory limits, or execution timeouts;
- no protection against a plugin that blocks a thread indefinitely or calls
  `Environment.Exit`;
- no isolation from process-wide static state in the default load context;
- no secret store or credential protection;
- no audit trail beyond the configured `IRuntimeLogger`;
- no plugin repository/feed or update mechanism.

## Deployment guidance

Only install plugins from sources you trust. For production hosts:

1. Run the host under a dedicated, least-privileged account.
2. Restrict write access to the plugin root. If an attacker can replace plugin
   files, they can execute arbitrary code with the host's identity.
3. Treat plugin manifests, assemblies, dependencies, and native libraries as
   executable supply-chain artifacts. Review or verify them before deployment.
4. Keep private dependencies inside the plugin directory and avoid relying on
   assemblies from unrelated locations.
5. Use OS-level isolation (container, virtual machine, restricted account, or
   job objects) when running code you do not fully trust. Such isolation is
   external to ModularRuntime and is not implemented by this repository.
6. Avoid placing secrets in the host process environment when untrusted plugins
   run in that process.
7. Monitor host logs and process health. A plugin failure is isolated as a
   lifecycle result, but a process crash or hang is not.

## IPC security

Since v0.3 the runtime is controlled through a named pipe served by
`ModularRuntime.Daemon` (protocol in [IPC_PROTOCOL.md](IPC_PROTOCOL.md)). The
trust boundary is the **Windows user account**.

Controls:

- **Local only.** Named pipes only; no TCP or HTTP listener. The pipe DACL has an
  explicit deny entry for the `NETWORK` SID, so network logons are refused even
  for the same account.
- **Current user only.** The DACL is protected (no inherited entries) and grants
  access to exactly one SID: the user running the daemon. There are no
  Everyone, Authenticated Users, or Administrators entries. The pipe owner is
  always the user SID, also when the daemon runs elevated.
- **Server authentication by the client.** Right after connecting, before
  sending anything, the client reads the pipe's owner and refuses the connection
  unless it is the current user's SID (or the token's default owner, used by
  pre-0.3.0 elevated daemons), so another account cannot impersonate the daemon.
  .NET's `PipeOptions.CurrentUserOnly` is deliberately not used: it compares with
  the token's default owner, which is `Administrators` in an elevated process, so
  an elevated CLI could not reach a normal daemon of the same user (found during
  v0.3 validation and fixed).
- **No squatting.** The daemon creates its first pipe instance with
  `FirstPipeInstance`: if any process already owns that pipe name, the daemon
  refuses to start (exit code 2) instead of sharing it. The pipe name embeds a
  hash of the user SID and the profile path, and the single-instance mutex uses
  the same key.
- **Closed operation set.** Only the operations listed in the protocol exist:
  status, shutdown, plugin lifecycle by id, catalog snapshots, logs. There is no
  operation that executes code, evaluates expressions, reads or writes files,
  loads assemblies, publishes events, or invokes plugin services. Plugin folders
  come from the daemon's own configuration, never from a client request.
- **Validated input.** Payloads are deserialized into fixed DTOs; identifiers are
  length-limited and validated; log filters are validated; unknown operations and
  malformed messages are rejected.
- **Bounded resources.** Message size (1 MiB default), handshake timeout,
  connections, requests in flight, notification queues, and log subscriptions
  are all bounded; a misbehaving client affects only its own connection.
- **No implicit stop.** Closing a client never stops the daemon. Shutdown is an
  explicit operation, confirmed in the desktop app.

Limitations:

- Any process running as the same user (including malware running as that
  user) can connect and administer that user's daemon, including stopping it
  and reading its logs. This is the same boundary as the user's own files.
- An elevated and a non-elevated process of the same user are the same principal
  for the pipe DACL. Verified: an elevated CLI controls a non-elevated daemon. Not
  verified: a non-elevated client against an elevated daemon (Windows integrity
  labels could block it; run the daemon without elevation).
- Logs and diagnostics served over IPC may contain paths and stack traces (see
  below).
- Verified by tests: the DACL contents (`PipeAcl_GrantsOnlyTheCurrentUserAndDeniesNetworkLogons`)
  and the refusal of a second server on the same pipe. Connection attempts from
  another account or from a remote machine were not exercised (the test
  environment has a single local account).

## Plugin packages (v0.4)

The package manager adds authenticity and integrity checks, not isolation:

- Official packages and the official registry are signed with ECDSA P-256 keys held only by the
  ModularRuntime administrator; the daemon ships public keys only and accepts no keys from downloaded
  content, configuration files, or clients ([PLUGIN_SIGNING.md](PLUGIN_SIGNING.md)).
- Every installation is verified completely in daemon-owned staging (package hash from the
  signed registry, signature, SHA-256 and size of every file, safe paths, limits) before it is
  published atomically; no plugin code runs during verification
  ([PLUGIN_PACKAGE_FORMAT.md](PLUGIN_PACKAGE_FORMAT.md)).
- Managed installations are re-verified before every load, and their files are held open
  without write or delete sharing while the plugin is loaded, closing the window between check
  and load ([PLUGIN_SECURITY_MODEL.md](PLUGIN_SECURITY_MODEL.md)).
- Unsigned local packages require explicit acceptance, are always labelled "Local,
  unverified", and are never started automatically.
- The registry is protected against rollback by a monotonic revision and against indefinite
  replay by an expiry date ([OFFICIAL_REGISTRY.md](OFFICIAL_REGISTRY.md)).
- `--test-trust-store` (development only) makes the daemon trust test keys; it is never read
  from configuration, and its use is logged and visible in every client.

A valid signature means "made by the holder of a trusted key and unchanged since". It does not
mean the code is harmless. Plugins still run in-process with the user's permissions.

## Developer tool (v0.5)

- `dotnet modularruntime pack` produces **unsigned** local packages: an `integrity.json` declared
  by the tool and no `integrity.sig`. The daemon treats them as *Local, unverified*. The tool
  contains no signing code and no keys; official signatures are produced only by the
  administrator's offline signing tools after review and a controlled rebuild ([PLUGIN_SUBMISSION_GUIDE.md](PLUGIN_SUBMISSION_GUIDE.md)).
- `validate` and `inspect` never execute plugin code: assemblies are read with
  `System.Reflection.Metadata`, packages are verified by the daemon's `PackageVerifier` into a
  temporary directory, and builds happen only through `dotnet build` when requested (`--build`,
  `pack`). Building a project runs its MSBuild logic, so build only projects you trust.
- `inspect` rejects a signature by an unknown key and never shows it as "unsigned". Test keys
  are trusted only with an explicit `--test-trust-store`.
- `prepare-submission` excludes build output, IDE state, keys, certificates, credential files,
  files containing PEM private keys, and NuGet configurations with credentials. It does not
  follow symbolic links. Its hashes identify the delivery and are not an approval.

## Official publishing (v0.6)

- An official signature is produced only on the offline signing machine, only for the exact
  `integrity.json` of an accepted controlled build, and only under an approval signed with a
  approvals key that the review policy maps to the administrator's identity. Unsigned approvals
  work only with test keys.
- Registry, package, and approval keys are separate; production keys have one purpose; the
  signing tools check that a private key matches its trusted public key before signing. Those
  tools are not part of this repository; verification is (`PackageVerifier`,
  `RegistryCatalogCheck`, `dotnet modularruntime inspect` and `registry-check`).
- Submitted code is validated statically (never executed) and built only in a controlled build
  with a minimal environment, allowed package sources, locked restore, and — for production —
  container or ephemeral-runner isolation; the build refuses to run where signing material is
  configured.
- Withdrawals and revocations are signed catalog changes; the daemon applies revocations before
  every load, offline too, and never trusts an unsigned revocation
  ([PACKAGE_REVOCATION.md](PACKAGE_REVOCATION.md)).
- The registry repository uses read-only workflow tokens, no secrets, actions pinned by commit
  SHA, and no `pull_request_target`; its CI verifies catalogs with the public
  `dotnet modularruntime registry-check` and nothing in it signs or publishes.
- None of this proves that a plugin is safe: reviews and automated checks reduce risk; a
  signature establishes origin and integrity.

## Path handling

The entry assembly path is validated lexically:

- rooted paths and drive/colon syntax are rejected;
- leading/trailing whitespace and NUL characters are rejected;
- the resolved path must not escape the plugin directory;
- the file must have a `.dll` extension and exist.

The check does not resolve symbolic links, junctions, or other reparse points.
An attacker who can create links inside an otherwise trusted plugin directory
could point outside it. Because the plugin directory is already trusted to
contain executable code, this is not a boundary against a malicious
installation; it is a defense against malformed or accidental paths. Use
filesystem ACLs to prevent untrusted users from creating links there.

## Error and log exposure

Plugin exceptions, paths, and messages may be included in:

- `PluginOperationResult` returned to the embedding application;
- event delivery results and service/contribution invocation results
  (`PluginInvocationResult`), including handler-reported messages and
  `PluginDiagnostic` text of handler exceptions, returned to callers that may be
  *other plugins*;
- `PluginRuntimeInfo.LastError` (string-only, retained by the engine);
- entries sent to `IRuntimeLogger`.

The engine deliberately does not retain exception objects, but the messages can
still contain sensitive information supplied by a plugin or by the environment.
Configure logging and result handling accordingly. The daemon writes
exception details to its log file (`<profile>\logs`) and serves them to IPC
clients of the same user (`logs.query`, `plugins.get`), and the CLI and desktop
app display them. Since v0.1.1 these details are
carried as `PluginDiagnostic` text, which includes full stack traces and can
include file-system paths of the build machine and of the plugin installation.

## Dependency identity

`ModularRuntime.Contracts` is always resolved from the runtime's default load
context, never from a plugin-local copy. The loader also checks the entry
assembly's contracts reference version (same major, and same minor while the
API is 0.x). This prevents accidental
contract-type duplication across plugin boundaries. It does not prevent a
plugin from defining its own unrelated types with similar names.

Private managed and unmanaged dependencies are resolved through
`AssemblyDependencyResolver` and the plugin directory. A plugin can still load
additional assemblies explicitly; the runtime cannot prevent that.

## Reporting security issues

ModularRuntime (pre-release) does not define a formal security-reporting process. Until one is added,
treat security-sensitive deployments as trusted-code deployments and apply the
controls above.
