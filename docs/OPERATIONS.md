# Operations: the daemon, its clients, and the plugin manager

ModularRuntime runs as three processes with strictly separated lifetimes:

```text
 ModularRuntime.Desktop.exe ──┐                         ┌── plugins (collectible ALCs)
                              ├── named pipe (IPC v1) ──┤   events, services, streams,
 ModularRuntime.Cli.exe ──────┘    current user only    │   extensions, ownership
                                                        └── ModularRuntime.Daemon.exe
```

The daemon is the only process that loads plugins or holds runtime state. The clients hold a
connection and a view; losing them loses nothing. The protocol is in
[IPC_PROTOCOL.md](IPC_PROTOCOL.md), its security model in [SECURITY.md](SECURITY.md#ipc-security).

## Profiles

All state lives under a profile directory: `--profile`, else `MODULARRUNTIME_PROFILE`, else
`%LOCALAPPDATA%\ModularRuntime`. Each profile has its own pipe and single-instance lock (both
derived from the user SID and the profile path), so separate profiles run separate daemons.

```text
<profile>/
  config/daemon.json              pluginDirectory, logBufferCapacity, shutdownTimeoutSeconds,
                                  autoStartPlugins, maxMessageBytes, registryLocation (unknown keys rejected)
  logs/daemon-*.log               daily log files
  plugins/<id>/                   installations (managed) and hand-copied folders (unmanaged)
  plugins/.staging/<operation>/   daemon-owned staging (same volume as plugins/, so moves are atomic)
  packages/installed/<id>/        record.json, integrity.json (exact bytes), integrity.sig (official)
  packages/registry/              cached registry.json + registry.sig, state.json (highest revision)
  runtime/daemon.json             pid, ids, version, start time, pipe name of the running daemon
  desktop/settings.json           desktop preferences (theme, start daemon on launch, log auto-scroll)
```

## The daemon

`ModularRuntime.Daemon.exe [--profile <dir>] [--plugins <dir>] [--log-buffer <n>] [--shutdown-timeout <sec>] [--auto-start] [--registry <url|dir>] [--test-trust-store <file>]`

Start:

1. Parse arguments and `config/daemon.json` (arguments win). Invalid input: exit code **4**. The
   daemon is a GUI-subsystem executable without a window; for `--help` and argument errors it
   attaches to the parent's console, if any.
2. Create the profile folders and start the log store (ring buffer plus daily file).
3. Acquire the single-instance mutex `Local\ModularRuntime.Daemon.<key>`. If another daemon holds
   it, log a line and exit with code **2**. An abandoned mutex (previous daemon crashed) is taken
   over and logged.
4. Complete or roll back package operations interrupted by a crash (journal in
   `plugins/.staging`), load the cached registry (re-verified, offline-capable), create the
   `PluginRuntime` with the package load verifier, discover plugins, start the IPC server on the
   profile's pipe (`FirstPipeInstance`: a squatter cannot be joined silently; failure also exits
   with **2**), and write `runtime/daemon.json`.
5. With `--auto-start` (or `autoStartPlugins`), start plugins in start order, except local
   packages without an official signature, which are never started automatically.
6. Serve until a shutdown is requested. Startup failures after the lock is acquired exit with
   **1** and are logged as `Critical`.

Any number of processes may try to start a daemon for the same profile at the same time: exactly
one acquires the mutex, the others exit with code 2. `DaemonLauncher.EnsureRunningAsync` (CLI and
desktop app) starts a daemon if none answers a handshake and polls with backoff (100 ms → 1 s,
default limit 20 s) until some daemon accepts; only an exit code other than 0 and 2 is reported as
a startup failure.

Shutdown is requested by `runtime.shutdown` over IPC (`modularruntime daemon stop`, or *Stop
daemon* in the desktop app, which asks for confirmation and states how many plugins are running),
by `SIGTERM`/`SIGINT`/`SIGHUP`/`SIGQUIT` as delivered by .NET on Windows (console close, Ctrl+C,
logoff, system shutdown), or by `ProcessExit`. The sequence, run once:

1. State becomes `Stopping`; `daemon.stopping` is broadcast; new lifecycle requests get
   `daemon_stopping`; package operations are canceled and given up to 20 s.
2. The `PluginRuntime` is disposed: plugins stopped in reverse start order, owned resources
   released, load contexts unloaded, bounded by the shutdown timeout (default **30 s**). If a
   plugin ignores cancellation past it, the daemon logs a `Critical` entry and continues; exit
   code **3**.
3. The state notifier and the IPC server stop; connections close.
4. `runtime/daemon.json` is deleted, the final log line is written, the log file is flushed, the
   mutex is released.

| Exit code | Meaning |
|---|---|
| 0 | Graceful shutdown completed |
| 1 | Startup failed |
| 2 | Another daemon already serves this profile (nothing was done) |
| 3 | Shutdown did not complete within the timeout; the process exited anyway |
| 4 | Invalid arguments or configuration |

`Process.Kill` is never used to stop the daemon in normal operation. The daemon is not a Windows
Service and does not start at logon; it runs while the user's session runs. A launcher inside a
kill-on-close job object that forbids breakaway (some debuggers, CI agents, terminals) can still
take the daemon down when the job closes. Two interactive sessions of the same user share pipe
names, so the second daemon for the same profile is expected to exit with code 2 (not verified by
tests).

## CLI

```text
modularruntime [--profile <dir>] [--json] [--timeout <sec>] <command>

status                          Daemon state, version, uptime, and counts
daemon start [--auto-start]     Start the daemon if it is not running
daemon stop                     Stop the daemon gracefully (stops all plugins); reports its exit code
plugins list | info <id> | start <id> | stop <id> | restart <id> | start-all | stop-all
plugins packages | updates | verify <id>
plugins install <id> [--version <v>]
plugins install-zip <file.zip> [--allow-unverified] [--stop-if-running]
plugins update <id> [--stop-if-running]
plugins remove <id> [--stop-if-running] [--yes]
registry status | refresh | list | info <id>
services list                   kind (json/binary/typed), version, availability, operations or contract
streams list                    streams (item type, subscribers, written, buffer) and loaded shared contracts
extensions list
events subscriptions
logs [--follow] [--level <level>] [--plugin <id>] [--category <name>] [--search <text>] [--limit <n>]
```

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | The operation ran and failed (plugin failure, verification, daemon exited with a non-zero code) |
| 2 | Usage error |
| 3 | Daemon unavailable (not running, unreachable, could not be started) |
| 4 | The daemon rejected the request (validation, unknown plugin, daemon stopping, incompatible protocol version, `plugin_running`, `unverified_not_accepted`, `registry_rollback`, …) |
| 130 | Canceled (Ctrl+C) |

`daemon start` returns once the daemon accepts connections; `daemon stop` opens a handle to the
daemon process before sending the request and waits for it to exit. `start-all`/`stop-all` treat
plugins already in the target state as success. `logs --follow` subscribes, prints history, fills
the gap by sequence, and reports dropped entries. Unsigned ZIPs need a typed confirmation
interactively, or `--allow-unverified` in scripts. `--json` works for every command.

The CLI is a client: it cannot publish events, invoke services, or write streams. IPC exposes only
validated administrative operations.

## Desktop application

WPF on `net10.0-windows`, MVVM without third-party libraries, dark and light themes, per-monitor
DPI, custom window chrome. Pages: Dashboard (daemon state, counts, clients, folders), Store (the
authenticated catalog, Updates, Install from ZIP), Plugins (installed plugins: start, stop,
restart, verify, remove, details with owned resources and the last error), Events, Services
(with the service kind and contract), Extensions, Streams (streams and loaded shared contracts),
Logs (live stream with filters, pause, markers), Settings (theme, start daemon on launch, log
auto-scroll, Start/Stop daemon).

`RuntimeConnection` supervises one connection: connects to the profile's pipe and, if enabled and
no daemon answers, starts one on the first attempt only; subscribes to `state.changed` and reloads
snapshots on change; on loss shows Disconnected, clears stale data, reconnects with backoff
(1 s → 10 s), offers *Retry* and *Start daemon*; never restarts a daemon that stopped on its own,
and after an explicit stop waits for the user. Lifecycle commands never update state
optimistically; the list is refreshed from the daemon after the reply. Closing the window disposes
the connection and sends no shutdown request: the daemon and its plugins keep running.

Command-line options: `--profile <dir>`, `--no-autostart`.

## Plugin manager

Only the daemon writes installations; the GUI and CLI send requests.

| Operation | What the daemon does |
|---|---|
| Install official | Find the entry in the verified catalog → compatibility → download (size-limited) into staging → verify (hash from catalog, signature, every file) → publish atomically → register (not started) |
| Install local ZIP | Client uploads the file in chunks to staging → *inspect* verifies it and returns a preview → *install* (unsigned packages need `acceptUnverified`) → publish → register |
| Verify | Signature (official) and every file's size and SHA-256 against the recorded manifest; loads nothing |
| Update | New package verified first; then (only with `stopIfRunning` if running) stop → unregister → wait up to 15 s for unload → swap → register; any failure re-registers the previous installation unchanged (`files_in_use` when the old context still locks files) |
| Remove | Same take-out sequence, then atomic removal of the installation and record |
| Check updates | Installed managed packages vs. the highest published version in the cached catalog |

Operations run in the background with an ID, stage, progress, and result, broadcast as
`packages.operation` notifications; one operation per plugin at a time, at most two running
concurrently; cancellation is honored until the swap starts. Installations are published
atomically (directory rename of a fully verified tree) with a journal that crash recovery
completes or rolls back at the next start. Nothing is ever started automatically after an
installation or update.

Trust states shown everywhere:

| State | Meaning | Loaded? |
|---|---|---|
| Official verified | Official signature verified, every file matches | Yes, after re-verification at each start |
| Official (test key) | Same, with a test key trusted only on a development daemon | Yes |
| Local, unverified | Installed from a ZIP without an official signature; inventoried | Yes, on explicit start only; never auto-started |
| Modified | Files changed since installation | No |
| Verification failed | The official signature no longer verifies (bad, unknown, or revoked key) | No |
| Revoked | The signed catalog revoked this exact package or its signing key | No (a running instance keeps running until stopped); kept on disk |
| Incompatible | Wrong plugin API or runtime version | Not installable |
| Unmanaged | Copied into the plugin folder by hand; no package record | Yes; never shown as verified |

## Restart in the same daemon

A stopped or failed plugin is started again without restarting the daemon:
`PluginRuntime.StartAsync` renews the registration under a per-plugin gate (the installation is
read and validated again; for managed packages the signature and every file's SHA-256 are
verified again before the new load context is created), so `Ready → Running → Stopped → Running …`
works as often as needed and never reuses anything from the previous instance.
`plugins restart` / `plugins.restart` is a stop followed by that start; *Start all* restarts
stopped plugins but does not retry failed ones. A plugin that keeps references to its own load
context keeps the old context in memory (and may keep its files locked) until it lets go; see
[LIFECYCLE.md](LIFECYCLE.md#restart).

## Verified scenarios

`ModularRuntime.Daemon.Tests` starts the built executables (`artifacts/app/<Configuration>/`),
each test with its own temporary profile, never the default one; three tests start the WPF app
and need an interactive desktop session. A running daemon locks the files in
`artifacts/app/<Configuration>/`, so stop it before rebuilding that configuration.

| Scenario | Verified |
|---|---|
| A client disconnects | daemon and plugins keep running; the client disappears from the list |
| The desktop window is closed / reopened; desktop and CLI together | same daemon pid; one daemon; CLI commands succeed |
| 4 daemons + 3 launchers started concurrently | exactly one daemon; the others exit with code 2 |
| CLI killed mid-stream, half frame, desktop killed | engine keeps running; only those connections close |
| Daemon killed | clients detect it; the desktop app survives and does not restart it |
| Explicit shutdown | exit 0, plugins and services shut down, final log lines present, runtime info removed |
| Restart, shutdown timeout (plugin ignoring cancellation: exit 3, CLI exit 1), operations while stopping (`daemon_stopping`), logs and state streaming | |
| Package manager | official install → verify → run → tamper → rejected load → removal; unsigned ZIP refused without acceptance, accepted as local, never auto-started; update requiring `--stop-if-running`; altered packages; registry rollback and tampering; daemon killed mid-installation and recovered; progress broadcast to every client |
| Start/stop/start and restart in the same daemon, re-verified before every load | |
| Typed services between two generated plugins sharing one contract assembly, `services list` and `streams list`, removal after unload | `tests/ModularRuntime.Template.Tests` scenario I |

## Publishing the application

```powershell
./scripts/publish.ps1 [-Configuration Release] [-Runtime win-x64] [-Output <dir>] [-SelfContained]
```

Publishes the three executables into one folder (default `artifacts/publish/win-x64`),
framework-dependent unless `-SelfContained`. The clients locate the daemon next to themselves or
through `MODULARRUNTIME_DAEMON_PATH`.
