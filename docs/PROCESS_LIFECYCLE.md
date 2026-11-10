# Process lifecycle

v0.3 splits ModularRuntime into three processes with strictly separated
lifetimes:

```text
 ModularRuntime.Desktop.exe ──┐                         ┌── plugins (collectible ALCs)
                              ├── named pipe (IPC v1) ──┤   event bus, services,
 ModularRuntime.Cli.exe ──────┘    current user only    │   extensions, ownership
                                                        └── ModularRuntime.Daemon.exe
```

The daemon is the only process that loads plugins or holds runtime state. The
clients hold a connection and a view; losing them loses nothing.

## The daemon

### Start

`ModularRuntime.Daemon.exe [--profile <dir>] [--plugins <dir>] [--log-buffer <n>] [--shutdown-timeout <sec>] [--auto-start]`

1. Parse arguments and `config/daemon.json` (unknown properties are rejected;
   arguments win over the file). Invalid input: exit code **4**. The daemon is a
   GUI-subsystem executable without a window; for `--help` and argument errors it
   attaches to the parent's console, if there is one, to print the message.
2. Create the profile folders and start the log store (ring buffer plus daily
   file `logs/daemon-yyyyMMdd.log`).
3. Acquire the single-instance mutex `Local\ModularRuntime.Daemon.<key>` (same
   key as the pipe name: user SID + profile). If another daemon holds it, log a
   line and exit with code **2** without touching anything else. An abandoned
   mutex (previous daemon crashed) is taken over and logged as a warning.
4. Complete or roll back package operations interrupted by a crash (journal in
   `plugins/.staging`), load the cached registry (re-verified, offline-capable), create the
   `PluginRuntime` with the package load verifier, discover plugins, start the IPC server on the
   profile's pipe (created with `FirstPipeInstance`, so a squatter cannot be
   joined silently; failure to create it also exits with **2**), and write
   `runtime/daemon.json` (pid, instance and runtime ids, version, start time, pipe name).
5. With `--auto-start` (or `autoStartPlugins` in the config file), start plugins in start
   order, except local packages without an official signature, which are never started
   automatically.
6. Serve until a shutdown is requested.

Startup failures after the lock is acquired exit with code **1** and are logged
as `Critical`.

### Single instance and concurrent starts

Any number of processes may try to start a daemon for the same profile at the
same time (two GUIs, a GUI and a CLI, scripts). Exactly one acquires the mutex;
the others exit with code 2. `DaemonLauncher.EnsureRunningAsync`, used by the
CLI and the desktop app, does not care which process won: it starts a daemon if
none answers a handshake, then polls with backoff (100 ms → 1 s, default limit
20 s) until **some** daemon accepts the handshake. Only an exit code other than
0 and 2 during that wait is reported as a startup failure.

### Shutdown

A shutdown is requested by:

- `runtime.shutdown` over IPC (`ModularRuntime.Cli daemon stop`, or *Stop daemon*
  in the desktop app, which asks for confirmation and states how many plugins are
  running);
- `SIGTERM`, `SIGINT`, `SIGHUP`, `SIGQUIT` as delivered by .NET on Windows
  (console close, Ctrl+C when attached, logoff, system shutdown);
- `ProcessExit`.

The sequence, run once:

1. State becomes `Stopping`; `daemon.stopping` is broadcast to every
   connection; new lifecycle requests get `daemon_stopping`.
2. The `PluginRuntime` is disposed: all plugins stopped in reverse start order,
   owned resources released, load contexts unloaded. This is bounded by the
   shutdown timeout (default **30 s**). If it does not finish (a plugin ignores
   cancellation), the daemon logs a `Critical` entry and continues; exit code
   **3**.
3. The state notifier and the IPC server stop; connections close, which is how
   clients learn that the daemon is gone.
4. `runtime/daemon.json` is deleted, the final log line is written
   (`Daemon stopped (exit code N).`), the log file is flushed, the mutex is
   released.

`ModularRuntime.Cli daemon stop` opens a handle to the daemon process before it
sends the request, waits for the process to exit (at most 11 minutes: the largest
configurable shutdown timeout plus a margin), and reports the daemon's real exit code: exit
**0** if the daemon exited with 0, **1** otherwise (for example
`shutdown timeout`).

For a session end the operating system bounds how long the daemon may take;
the shutdown is best effort there.

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Graceful shutdown completed |
| 1 | Startup failed |
| 2 | Another daemon already serves this profile (nothing was done) |
| 3 | Shutdown did not complete within the timeout; the process exited anyway |
| 4 | Invalid arguments or configuration |

`Process.Kill` is never used to stop the daemon in normal operation. The
automated tests use it only to simulate crashes and to clean up after failed
tests.

## The clients

### Starting the daemon from a client

The CLI (`daemon start`) and the desktop app (automatic at launch if enabled, or
*Start daemon*) both call `DaemonLauncher`:

- The executable is resolved as: explicit path → `MODULARRUNTIME_DAEMON_PATH` →
  `ModularRuntime.Daemon.exe` next to the running application. The build output
  (`artifacts/app/<Configuration>/`) and the publish output both place the three
  executables together.
- The daemon is created directly with `CreateProcess` — no `cmd.exe`, no
  PowerShell — with **no handle inheritance**, no console window, in its own
  process group, with the daemon's folder as working directory, and with
  `CREATE_BREAKAWAY_FROM_JOB` when the caller's job object allows it. The
  launcher keeps a process handle only while it waits for the handshake.
- Not inheriting handles matters: `Process.Start` always inherits every
  inheritable handle, so a daemon started that way by a CLI whose output is
  redirected keeps the CLI's stdout pipe open for its whole life, and the script
  reading that output never sees end-of-file. The real-process tests caught this.

### Closing a client

- Closing the desktop window (X, Alt+F4, taskbar) disposes the connection
  supervisor and the IPC client. It sends no shutdown request; the daemon only
  sees a client disconnect and removes that client's subscriptions.
- A CLI command connects, performs one operation, and disconnects.
  `logs --follow` stays connected until Ctrl+C.
- A client that crashes or is killed is the same as a disconnect for the daemon.
  A half-written frame is a protocol error for that connection only.

### Losing the daemon

When the daemon exits or crashes, every pending request fails with
`connection_lost` and the client's `Completion` task completes. The desktop app:

- stays open, clears data that would be stale, and shows **Disconnected**;
- reconnects with exponential backoff (1 s, 2 s, 4 s … capped at 10 s); *Retry*
  reconnects immediately;
- does **not** restart a daemon that went away on its own; *Start daemon* does,
  explicitly;
- after the user stopped the daemon from the app, waits for *Retry* or *Start
  daemon* instead of polling;
- on reconnection, reloads every snapshot; a different `daemonInstanceId` tells
  it the daemon was restarted (the logs page marks the restart).

The automatic start at launch happens only on the first connection attempt, and
only when enabled.

## Package operations and shutdown

Package operations (install, update, removal) run in the daemon as tracked background
operations. On shutdown the daemon cancels them and waits up to 20 s before stopping the
runtime; an operation interrupted at any point (also by a crash or `Process.Kill`) is completed
or rolled back by the journal recovery at the next start, so an installation directory is
always either the previous version or the new, fully verified one. Clients that reconnect list
running operations again (`packages.operations`).

Updates and removals of a running plugin stop it first only when the request says so
(`stopIfRunning`), then unregister it and wait up to 15 s for its load context to unload before
moving files; if the files are still in use, the previous installation is registered again and
the operation fails with `files_in_use`.

## Caveats

- A launcher running inside a job object with kill-on-close that forbids
  breakaway (some debuggers, CI agents, terminals) can still take the daemon down
  when the job closes. This is outside the application's control.
- The daemon is not a Windows Service and does not start at logon; it runs while
  the user's session runs.
- The mutex is in the session-local namespace: two interactive sessions of the
  same user (for example, console and RDP) can each run a daemon for the same
  profile directory, but they share the pipe name (pipes are global), so the
  second one is expected to fail creating its pipe and exit with code 2 (not
  verified: the tests run in one session).

## Verified scenarios

The `ModularRuntime.Daemon.Tests` real-process tests start the built
executables, each with its own temporary profile:

| Scenario | Verified |
| --- | --- |
| A — a client disconnects | daemon and plugins keep running; the client disappears from the list |
| B — the desktop window is closed (WM_CLOSE) | same daemon pid keeps running with the plugin `Running` |
| C — the desktop app is reopened | connects to the same daemon; still one daemon process |
| D — desktop app and CLI together | both listed as clients of one daemon; CLI commands succeed |
| E — 4 daemons + 3 launchers started concurrently | exactly one daemon; the others exit with code 2 |
| F — CLI killed mid-stream, half frame, desktop killed | engine keeps running; only those connections close |
| G — daemon killed | clients detect it; the desktop app survives and does not restart it |
| H — explicit shutdown | exit 0, plugins and services shut down, final log lines present, runtime info removed |
| Restart | a new daemon instance serves the profile; clients connect to it |
| Shutdown timeout | plugin ignoring cancellation: daemon exits with 3, CLI exits with 1 |
| Operations while stopping | rejected with `daemon_stopping` |
| Logs and state streaming | entries and `state.changed` arrive; unsubscribe works |
