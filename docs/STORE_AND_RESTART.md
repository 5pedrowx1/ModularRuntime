# Store page and plugin restart

This change separates discovering plugins (**Store**) from managing installed plugins
(**Plugins**) in the desktop app, and makes a stopped plugin startable again in the same daemon:
`Ready → Running → Stopped → Running → Stopped …`, as often as needed, without restarting the
daemon and without weakening any verification.

## Restart of a stopped plugin

### Root cause

The engine's slot states `Stopped` and `Failed` are terminal (by design since v0.1: a dirty
instance is never reused), and a plugin's factory is single-use. Stopping a plugin shuts the
instance down, releases the factory, and requests unload of its `AssemblyLoadContext`. Before
this change nothing created a new registration afterwards, so `plugins.start` on a stopped
plugin was rejected with `invalid_state` until the daemon restarted.

Reproduced against the real daemon before the fix:

```
plugins start com.example.hello   → Running
plugins stop  com.example.hello   → Stopped
plugins start com.example.hello   → rejected [invalid_state]
```

### Fix: renewal in `PluginRuntime`

The engine rule is unchanged. `PluginRuntime.StartAsync` renews the registration when the
plugin is `Stopped` or `Failed`:

1. A per-plugin gate serializes renewal; the state is checked again inside the gate, so
   concurrent starts produce exactly one new instance.
2. The installation directory is **read and validated again** (`plugin.json`, entry assembly,
   API version, compatibility). If it is no longer valid the start fails with
   `factory_failed` and the stopped registration is kept for diagnostics.
3. The old registration is unregistered. A weak reference to its load context is kept only
   so `WaitForUnloadAsync` can report collection; nothing of the previous instance is reused.
4. A new candidate is registered with a **new factory**, which creates a **new
   `AssemblyLoadContext`** on load. For a managed package the load verifier
   (`IPluginLoadVerifier`) checks the signature and every file's SHA-256 against the
   installation record before the assembly is loaded, exactly as on the first start.
   A modified installation is rejected ("was not loaded"); verification is never skipped.
5. The engine starts the new registration: a new instance ID, a new plugin context,
   a new ownership scope.

`RestartAsync` (IPC `plugins.restart`, CLI `plugins restart <id>`, desktop *Restart*) is a real
stop followed by that start. `StartAllAsync` restarts stopped plugins; failed plugins are only
retried by an explicit start (so *Start all* does not loop on a broken plugin).

### Evidence

- `PluginRestartTests` (integration): five start/stop cycles with a new instance each time;
  restart of a running and of a stopped plugin; concurrent starts produce one instance; a
  failed plugin can be retried and reports its diagnostic; start-all restarts stopped but not
  failed plugins; an installation changed while stopped is read again; old load contexts are
  collected after repeated restarts (`WaitForUnloadAsync` returns true — no retention of old
  assemblies).
- `InstallationTests.StartStopStart_ReverifiesTheInstallationBeforeEveryNewLoad`: three cycles
  of a signed package, then a tampered file makes both start and restart fail before loading.
- `ProcessIndependenceTests.PluginStartStopStart_AndRestart_WorkRepeatedlyInTheSameDaemon`
  (real daemon + CLI + desktop app): four IPC cycles, then CLI start/restart/stop/start; every
  instance ID differs, the daemon PID never changes, and closing the desktop app leaves the
  daemon and the plugin running.
- `PackageManagerProcessTests.InstalledPlugin_StartStopStart_InTheSameDaemon_IsReverifiedBeforeEveryLoad`
  (real daemon, official test-signed package): three cycles and a restart, then a modified
  installed file makes start and restart fail with "was not loaded".

## Desktop: Store and Plugins

Sidebar order: Dashboard, **Store**, **Plugins**, Events, Services, Extensions, Logs, Settings.

### Store

- Cards for the authenticated official catalog (rows are virtualized; the number of cards per
  row follows the window width). A card shows the plugin's initial (the registry has no icons),
  name, author, short description, category, version, signature trust (*Official*,
  *Official (test key)*, *Signing key not trusted*), installation state, **Show more** and the
  action (*Install*, *Update*, *Installed*, *Incompatible*).
- **Show more** opens a details panel: full description, version, author, category, runtime
  and API compatibility, publication date, size, package SHA-256, signing key, source and
  license when present, release notes, and what is verified at installation.
- Search (name, author, description), category filter, *Refresh catalog*, an **Updates** tab
  with the installed plugins that have a newer catalog version.
- **Install from ZIP** in the header: the existing flow (upload to the daemon, the daemon's
  verification and preview, explicit acknowledgement for unsigned packages) is unchanged.
- Everything comes from the daemon over IPC. Progress of package operations is shown by a
  tracker shared with the Plugins page; success is reported only when the daemon completes.

### Plugins

Installed plugins only: list, search, state filter (All, Running, Ready, Stopped, Failed),
name, ID, version, state, trust; row actions *Start* (Ready, Stopped, Failed), *Restart* and
*Stop* (Running); a details panel with *Start*, *Stop*, *Restart*, *Verify integrity*,
*Remove…*, the package (source, signing key, SHA-256, files, last verification), engine
details (instance ID, API version, start order, entry type, timestamps), owned resources and
the last error with its diagnostic (*Copy*). There is no catalog and no package installation
on this page; a newer version is pointed to the Store's Updates tab.

## Limitations

- A plugin that keeps references to its own load context (static roots, threads it never
  stops) prevents that context from being collected; restart still works (a new context is
  created each time) but memory of old contexts is retained until the plugin releases them.
- While an old context is not yet collected its assembly files may stay locked by Windows, so
  an update right after a stop can wait for collection.
- The registry has no plugin icons; cards show a monogram.
