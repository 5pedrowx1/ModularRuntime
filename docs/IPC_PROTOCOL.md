# IPC protocol (version 1)

`ModularRuntime.Daemon` is controlled exclusively through this protocol. The CLI
and the desktop app are ordinary clients; nothing else is shared between the
processes. Implementation: `src/ModularRuntime.Ipc.Contracts` (messages, names,
DTOs) and `src/ModularRuntime.Ipc` (framing, server, client).

## Transport

- **Windows named pipe**, local only, one pipe per user profile:
  `ModularRuntime.ipc1.<key>`, where `<key>` is the first 24 hex characters of
  `SHA-256(<user SID> | <PROFILE DIRECTORY, upper case>)`. Different users and
  different profiles never share a pipe. No TCP port is opened.
- Byte mode, asynchronous, full duplex. Requests may be pipelined; responses
  arrive in any order and are correlated by id.
- Security: see [SECURITY.md](SECURITY.md#ipc-security). In short, the pipe DACL
  grants access only to the current user, denies the `NETWORK` SID, and the
  client verifies that the pipe is owned by the current user before talking.

## Framing

Every message is one frame:

```text
[ uint32 little-endian: body length in bytes ][ body: UTF-8 JSON object ]
```

- The length is validated **before** any allocation. `0` and anything above the
  connection's maximum are protocol errors.
- Default maximum message size: **1 MiB** (`IpcProtocol.DefaultMaxMessageBytes`);
  configurable in `config/daemon.json` (`maxMessageBytes`, 64 KiB – 16 MiB). The
  daemon announces its value in `welcome`.
- Reads tolerate partial reads (a frame may arrive one byte at a time). End of
  stream between frames is a clean close; end of stream inside a frame is a
  protocol error.
- JSON uses camelCase property names and enums as strings.

A framing error (bad length, oversized frame, invalid JSON, missing `type`)
means the stream position can no longer be trusted: the daemon sends an `error`
message with the code and closes the connection. Other connections and the
runtime are not affected.

## Message envelope

```json
{ "type": "request", "id": 12, "op": "plugins.start", "payload": { "pluginId": "com.example.hello" } }
```

| Field | Used by | Meaning |
| --- | --- | --- |
| `type` | all | `hello`, `welcome`, `request`, `response`, `cancel`, `notification`, `error` |
| `id` | request, response, cancel | Client-chosen request id (`long`), unique among the connection's requests in flight |
| `op` | request, notification | Operation or notification name |
| `payload` | most | Operation-specific JSON object |
| `error` | response, error | `{ "code", "message", "diagnostic"? }` |

`diagnostic` is a text-only snapshot (`exceptionType`, `message`, `stackTrace`,
`detail`); exception objects never cross the process boundary.

## Handshake

The first message on a connection must be `hello`, within **10 seconds**:

```json
{ "type": "hello", "payload": { "protocolVersion": 1, "clientName": "cli", "clientId": "6f1c…" } }
```

The daemon answers `welcome`:

```json
{ "type": "welcome", "payload": {
    "protocolVersion": 1,
    "daemonInstanceId": "…",   // changes every time a daemon process starts
    "runtimeId": "…",          // the PluginRuntime instance
    "daemonVersion": "0.3.0",
    "connectionId": "…",
    "maxMessageBytes": 1048576 } }
```

- Versions must match **exactly**. A mismatch is answered with an `error`
  (`protocol_version_mismatch`, message naming both versions) and the
  connection is closed. Clients show it as an incompatible daemon.
- Anything other than a valid `hello` first gets `handshake_required` or
  `invalid_message` and a close. A client that sends nothing is disconnected
  after the timeout.
- `clientName` (≤ 128 characters) is informational and listed by
  `runtime.status`. The name `probe` is reserved for liveness checks and hidden
  from the client list.

## Requests and responses

```json
{ "type": "response", "id": 12, "payload": { … } }
{ "type": "response", "id": 12, "error": { "code": "plugin_not_found", "message": "…" } }
```

- Each request runs independently; a slow operation does not block other
  requests on the same connection or on other connections.
- At most **32** requests in flight per connection (`too_many_requests`).
- A duplicate in-flight id, or a request without `id`/`op`, gets
  `invalid_request`.
- Operations are a closed set (below); anything else gets `unknown_operation`.
  There is no generic "execute", "eval", file, or assembly-loading operation.
- A payload that does not match the operation's schema gets `invalid_request`.
  Identifiers are length-limited (128) and validated (plugin ids follow the
  manifest id rules).
- A response that would exceed the maximum size is replaced by a
  `message_too_large` error response (narrow the request, e.g. a smaller log
  `limit`).
- An unexpected failure inside the daemon gets `internal_error` with a text
  diagnostic; the connection stays usable.

### Cancellation

```json
{ "type": "cancel", "id": 12 }
```

Cancels request 12 if it is still running; the request then completes with a
`canceled` error response. Cancelling a finished or unknown request is a no-op.
`RuntimeClient` sends `cancel` automatically when the caller's
`CancellationToken` fires. Lifecycle operations already handed to a plugin
observe the token cooperatively, like any in-process caller.

## Operations

| Operation | Payload | Result |
| --- | --- | --- |
| `runtime.status` | — | `RuntimeStatusDto`: daemon state, versions, ids, pid, start time, uptime, state version, folders, counts (plugins, services of every kind, extension points, subscriptions, `streamCount`), connected clients |
| `runtime.shutdown` | `{ reason? }` | `{ accepted, runningPluginCount }`; the daemon then stops gracefully (see [OPERATIONS.md](OPERATIONS.md)) |
| `plugins.list` | — | `{ stateVersion, plugins: PluginSummaryDto[] }` |
| `plugins.get` | `{ pluginId }` | `PluginDetailsDto`: summary, API version, entry, last error, owned resources |
| `plugins.start` / `plugins.stop` | `{ pluginId }` | `OperationResultDto` (status, resulting state, error code/message/diagnostic) |
| `plugins.restart` | `{ pluginId }` | `OperationResultDto`: stop (if running), then a start with a new registration, factory, and load context; managed packages are verified again before loading |
| `plugins.startAll` / `plugins.stopAll` | — | `{ results: OperationResultDto[] }` in start order (stop: reverse) |
| `events.subscriptions` | — | `{ stateVersion, subscriptions, topics }` |
| `services.list` | — | `{ stateVersion, services }`; each `ServiceDto` carries `kind` (`Json`, `Binary`, `Typed`), `operations` (empty for typed services), and `contractType` (typed services: the contract interface, assembly, and version as text) |
| `extensions.list` | — | `{ stateVersion, extensionPoints }` (with contributions) |
| `streams.list` | — | `{ stateVersion, streams, sharedContracts }`: `StreamDto` (ID, name, version, item type, metadata, owner, availability, subscriber count, items written, default buffer and policy) and the contract assemblies loaded into shared contexts (`name`, `version`, `users`) |
| `logs.query` | `{ filter?, afterSequence?, limit≤5000 }` | `{ entries, lastSequence, truncated }` |
| `logs.subscribe` | `{ filter? }` | `{ subscriptionId, lastSequence }` |
| `logs.unsubscribe` | `{ subscriptionId }` | — (`subscription_not_found` if unknown) |
| `state.subscribe` | — | `{ subscriptionId, stateVersion }` |
| `state.unsubscribe` | `{ subscriptionId }` | — |
| `registry.status` | — | `RegistryStatusDto`: configured, location, loaded revision, dates, signing key, test keys trusted, last error |
| `registry.refresh` | — | Downloads and authenticates the catalog; `RegistryStatusDto`, or a `registry_*` error |
| `registry.list` / `registry.get` | — / `{ pluginId }` | Latest entries with compatibility, installed version, update flag |
| `packages.list` / `packages.get` | — / `{ pluginId }` | `InstalledPackageDto`: source, trust state, signing key, package hash, file count, engine state, last verification, available update |
| `packages.verify` | `{ pluginId }` | `VerificationDto`: status (`Verified`, `Modified`, `SignatureInvalid`, `Missing`), message, problems |
| `packages.checkUpdates` | — | `{ registry, updates }` |
| `packages.installOfficial` | `{ pluginId, version? }` | `PackageOperationDto` (background operation) |
| `packages.update` / `packages.remove` | `{ pluginId, stopIfRunning }` | `PackageOperationDto` |
| `packages.upload.begin` | `{ fileName, size }` | `{ uploadId, maxChunkBytes }` (256 KiB) |
| `packages.upload.chunk` | `{ uploadId, offset, data (base64) }` | — ; chunks in order, size bounded by `begin` |
| `packages.upload.inspect` | `{ uploadId }` | `PackagePreviewDto`: the daemon's full verification of the staged copy; nothing installed |
| `packages.upload.install` | `{ uploadId, acceptUnverified, stopIfRunning }` | `PackageOperationDto` |
| `packages.upload.discard` | `{ uploadId }` | — |
| `packages.operations` / `packages.getOperation` | — / `{ operationId }` | Operations (running and recent) |
| `packages.cancel` | `{ operationId }` | Cancels before the installation swap starts |

Package operations are asynchronous: the request returns as soon as the operation is
accepted, and its progress arrives as `packages.operation` notifications (and through
`packages.getOperation`). Uploads belong to their connection and are discarded when it closes
or after 30 minutes of inactivity; the daemon never reads a client-supplied file path.

A lifecycle failure of a plugin is a **successful response** whose
`OperationResultDto.status` is `Failed`, `Rejected`, or `Canceled`, not an IPC error: the
request itself was processed. IPC errors are reserved for protocol, validation,
and availability problems.

While the daemon is stopping, `runtime.shutdown` and lifecycle operations are
rejected with `daemon_stopping`; read-only operations keep working until the
connection closes.

## Notifications

Server-initiated messages:

```json
{ "type": "notification", "op": "state.changed", "payload": { "stateVersion": 42 } }
```

| Notification | Sent to | Payload |
| --- | --- | --- |
| `state.changed` | `state.subscribe` subscribers | `{ stateVersion }` — the runtime changed (plugin state, services, streams, subscriptions, extensions, resources) |
| `logs.entries` | each log subscription | `{ subscriptionId, entries, droppedCount }` |
| `daemon.stopping` | every connection | `{ reason }` — shutdown has started |
| `packages.operation` | every connection | `PackageOperationDto` — stage, progress, message, error code, result |

### State synchronization

The engine keeps a monotonically increasing **state version**. A client
subscribes (`state.subscribe` returns the current version), loads snapshots
(`plugins.list`, … each carrying the version they reflect), and refreshes when
`state.changed` reports a newer version. Notifications are coalesced (at most
one per ~100 ms per subscriber) and carry no data, so a missed notification is
harmless: the next one, or a reconnection, triggers a fresh snapshot. After a
reconnection clients always reload, and a different `daemonInstanceId` tells
them the daemon was restarted.

### Logs

Log entries have a per-daemon increasing `sequence`. A client that wants a
gap-free view subscribes first, then queries history, and merges both by
sequence (dropping duplicates). After reconnecting it queries
`afterSequence = <last seen>`; `truncated: true` means the daemon's ring buffer
(default 5000 entries) no longer holds some of the missed entries. Sequence
numbers restart with a new daemon instance.

Streaming is batched (up to 200 entries or 50 ms). Each subscription has a
bounded queue (2000 entries); when a client falls behind, entries are dropped for
that subscription only and the next batch reports `droppedCount`. At most 8 log
subscriptions per connection.

### Backpressure

Each connection has a bounded notification queue (256 messages). The daemon never
blocks on a slow client: notifications that do not fit are dropped and counted
for that connection. Responses are written directly and are never dropped. A
client that stops reading eventually fills the pipe; only its own connection is
affected.

## Error codes

| Code | Meaning |
| --- | --- |
| `protocol_version_mismatch` | Handshake versions differ; connection closed |
| `handshake_required` | First message was not `hello` |
| `invalid_message` | Malformed frame or JSON, missing `type`, unexpected message type |
| `message_too_large` | Frame or response above the maximum size |
| `unknown_operation` | Operation not in the protocol |
| `invalid_request` | Missing id/op, duplicate id, bad payload, invalid identifier or filter |
| `too_many_requests` | More than 32 requests in flight on the connection |
| `canceled` | The request was canceled by the client |
| `internal_error` | Unexpected daemon failure (with diagnostic) |
| `daemon_stopping` | The daemon is shutting down |
| `plugin_not_found` | No plugin with that id |
| `operation_failed` | The operation could not be performed |
| `subscription_not_found` | Unknown subscription id |
| `operation_in_progress` | Another package operation is changing the same plugin |
| `operation_not_found` / `upload_not_found` | Unknown operation or upload (uploads are per connection) |
| `plugin_running` | Update or removal of a running plugin without `stopIfRunning` |
| `unverified_not_accepted` | Installing an unsigned package without `acceptUnverified` |
| `already_installed` / `not_managed` | Install of an installed plugin; update/remove/verify of a hand-copied plugin |
| `registry_*` | `registry_not_configured`, `registry_unavailable`, `registry_signature_invalid`, `registry_invalid`, `registry_rollback`, `registry_conflict`, `registry_expired`, `registry_not_yet_valid`, `registry_plugin_not_found` |
| package codes | Verification failures, for example `signature_invalid`, `integrity_mismatch`, `unsafe_entry_path`; see [PLUGIN_PACKAGE_FORMAT.md](PLUGIN_PACKAGE_FORMAT.md) |
| `connection_lost` | Client side: the connection closed while the request was pending |
| `daemon_unavailable` | Client side: no daemon could be reached |

## Limits summary

| Limit | Value |
| --- | --- |
| Message size | 1 MiB default, 16 MiB hard maximum |
| Handshake timeout | 10 s |
| Connections per daemon | 32 |
| Requests in flight per connection | 32 |
| Notification queue per connection | 256 |
| Log subscriptions per connection | 8 |
| Log subscription queue | 2000 entries |
| Log query limit | 5000 entries |
| Identifier length | 128 characters |
| Upload chunk | 256 KiB (base64 in the message) |
| Uploads per connection | 4 |
| Package operations running at once | 2 (one per plugin) |

## Compatibility

The protocol version is an integer. Any incompatible change (message shape,
semantics, required field) increments it, and clients and daemons of different
versions refuse each other at the handshake instead of misinterpreting
messages. Adding an operation or an optional field does not require a new
version; old clients never call new operations, and unknown fields are ignored
by readers. The pipe name includes `ipc1` so that a future protocol can run side
by side.
