# Plugin security model

## What the mechanisms mean

ModularRuntime runs plugins **in-process**, inside the daemon, with the permissions of the
Windows user that runs the daemon.

- **A digital signature** establishes that the package (its signed file list, and through it
  every file) was produced by the holder of a trusted key and has not changed since. It does
  **not** make code safe.
- **Code review and controlled builds** reduce the risk that an official package is
  malicious or vulnerable. They do not eliminate it.
- **SHA-256 checks** detect changes relative to authenticated hashes. Hashes that come from the
  same untrusted source as the files (an unsigned package's own `integrity.json`) only show
  internal consistency.
- **None of these is isolation.** There is no sandbox: a plugin can do anything the user can
  do (files, network, processes, other plugins' memory). `AssemblyLoadContext` isolates
  dependencies and identities, not privileges.

## Trust states

| State | Meaning | Loaded? |
| --- | --- | --- |
| Official verified | Official signature verified, every file matches | Yes, after re-verification at each start |
| Official (test key) | Same, with a test key trusted only on a development daemon | Yes |
| Local, unverified | Installed from a ZIP without an official signature; inventoried | Yes, on explicit start only; never auto-started |
| Modified | Files changed since installation | No |
| Verification failed | The official signature no longer verifies (bad, unknown, or revoked key) | No |
| Incompatible | Wrong plugin API or runtime version | Not installable |
| Unmanaged | Copied into the plugin folder by hand; no package record | Yes (as before v0.4); never shown as verified |
| Revoked (v0.6) | The signed catalog revoked this exact package or its signing key | No (a running instance keeps running until stopped); kept on disk |

Since v0.6 the catalog also carries a **status** per version: *withdrawn* versions are not
offered by the Store but keep working where installed; *revoked* versions are never installed
and never loaded again ([PACKAGE_REVOCATION.md](PACKAGE_REVOCATION.md)). Revocation information
is used only from a catalog whose signature verified with a registry key.

## Installation path

- Only the daemon writes installations. The GUI and CLI send requests; a local ZIP is
  **uploaded** to the daemon's staging area in chunks, and the daemon verifies its own copy,
  so later changes to the user's file have no effect.
- Every package is verified completely in staging before anything else happens
  ([PLUGIN_PACKAGE_FORMAT.md](PLUGIN_PACKAGE_FORMAT.md)); no DLL is loaded and no plugin code
  runs during verification.
- Unsigned packages require an explicit acknowledgement (GUI checkbox; CLI typed confirmation
  or `--allow-unverified`), are labelled "Local, unverified" everywhere, and are never started
  automatically, including by `--auto-start`.
- Installations are published atomically (directory rename of a fully verified tree) with a
  journal that crash recovery completes or rolls back at the next daemon start.

## Verification before load and the race window

Before a managed plugin's assemblies are loaded (every start), the Loader calls the daemon's
`PackageLoadVerifier`:

1. the recorded `integrity.json` and, for official packages, `integrity.sig` are re-verified
   with the **current** trust store (a key revoked since installation stops the load);
2. plugin ID and version are compared with `plugin.json` and the record;
3. the installation directory is enumerated: no reparse points or symbolic links, no extra
   files, no missing files;
4. each file is opened **for reading with read sharing only** and hashed through that handle.

The handles stay open while the plugin is loaded (released when the plugin stops). While
they are open no process can write, replace, rename, or delete those files or rename their
directories, so the bytes that were verified are the bytes the runtime loads; there is no
window between the check and the load in which a file could be swapped.

Limits of this protection (the trust boundary is the Windows user account):

- A process running as the same user can still kill or debug the daemon, inject into it,
  edit the daemon's own executables, change the profile's records or trust state files while
  the daemon is stopped, or start the daemon with `--test-trust-store`. Code signing of the
  product executables and OS-level protections are outside this version.
- After a plugin stops, the runtime may keep its assemblies mapped until the load context is
  collected; those files then stay locked. This is why updates and removals wait for unload
  (and report `files_in_use` with the previous state intact if it does not happen).
- Unmanaged (hand-copied) plugins are loaded without verification, as in earlier versions.
- A plugin may write files into its own installation directory; the next verification then
  reports it as modified. Plugins must store data elsewhere.

## Daemon authority

Clients cannot: write installation directories, mark anything verified, bypass signature
errors, replace files of running plugins (updates and removals stop the plugin first, with an
explicit `stopIfRunning` request), modify trusted keys, sign packages, or run arbitrary
administrative actions. All package operations are validated IPC requests from the current
Windows user ([SECURITY.md](SECURITY.md#ipc-security)).
