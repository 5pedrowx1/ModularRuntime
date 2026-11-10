# Plugin packaging (`dotnet modularruntime`)

`ModularRuntime.DevTools` is a .NET tool (command `dotnet-modularruntime`, invoked as
`dotnet modularruntime`). It works on files only: it needs neither the daemon nor the desktop app,
builds only through `dotnet build`, never loads or runs plugin code, and never signs.

```text
dotnet modularruntime validate [--project <path>] [-c <config>] [--build] [-v]
dotnet modularruntime pack [--project <path>] [-c <config>] [-o <file.zip|dir>] [--no-build] [--include-symbols] [-v]
dotnet modularruntime inspect <package.zip> [--test-trust-store <file>]
dotnet modularruntime prepare-submission [--project <path>] [--source-root <dir>] [-o <dir>] [--no-package]
                                         [--license <spdx>] [--source-repository <https url>] [--source-commit <sha>]
dotnet modularruntime doctor [--project <path>]
dotnet modularruntime --help | --version
```

Exit codes: `0` success, `1` validation, build, or package failure, `2` usage error, `130` canceled (Ctrl+C).

## validate

Static checks, using the same code as the runtime where a rule already exists:

| Check | Rule source |
|---|---|
| `plugin.json` exists, is valid JSON, has no unknown fields, and its fields are valid (ID, name, version, API version, entry assembly path, entry type, author, description, start order) | the Loader's manifest reader (`PluginLoader.ValidateManifestAsync`) |
| entry assembly equals the project's `AssemblyName.dll`; target framework is `net10.0`; references `ModularRuntime.Sdk` or `.Contracts`; no host-side references (Engine, Loader, Daemon, …); `EnableDynamicLoading`; `plugin.json` copied to the output; project version equals the manifest version | MSBuild evaluation (`dotnet msbuild -getProperty/-getItem`) |
| after a build: entry type exists, is public and concrete, implements `IPlugin`, has a public parameterless constructor; Contracts reference matches the plugin API | assembly **metadata** (`System.Reflection.Metadata`); nothing is loaded |
| runtime dependencies listed in `deps.json` exist in the output | deps.json |
| file names are packageable, no case-only collisions, within package limits | Packaging path rules (`PackagePaths`) |

### Diagnostics

| Code | Severity | Meaning |
|---|---|---|
| `manifest_missing` | error | no `plugin.json` next to the project |
| `manifest_invalid` | error | the Loader rejected a manifest field (the Loader's own code is shown, e.g. `[api_version_incompatible]`) |
| `entry_assembly_name_mismatch` | error | `entryAssembly` is not what the project builds |
| `target_framework_unsupported` | error | not `net10.0`, or multi-targeting |
| `contracts_reference_missing` | error | no ModularRuntime.Sdk/Contracts reference |
| `host_assembly_referenced` | error | references Engine, Loader, Daemon, Ipc, Packaging, Registry, Desktop, Cli, Publisher, or DevTools, or packages the shared Contracts assembly |
| `entry_assembly_missing` / `entry_assembly_invalid` | error | the built assembly is missing or unreadable |
| `entry_type_not_found` / `entry_type_not_a_plugin` / `entry_type_not_instantiable` | error | the entry type problem found from metadata |
| `api_version_mismatch` | error | compiled against Contracts of another plugin API |
| `dependency_missing` | error | a runtime dependency in deps.json is not in the output |
| `unsafe_path` / `path_collision` / `package_limits` | error | the files cannot be packaged |
| `dynamic_loading_disabled` / `manifest_not_copied` / `version_mismatch` | warning | likely mistakes |
| `not_built` | info | no build output yet (use `--build`) |

## pack

1. Evaluates the project and validates it. If it is invalid, nothing is built or written.
2. Builds with `dotnet build -c Release` (`--no-build` uses the existing output instead).
3. Stages the build output. Excluded: `ModularRuntime.Contracts.*` (shared with the host),
   `.pdb` (unless `--include-symbols`), XML documentation next to assemblies,
   `*.runtimeconfig.dev.json`, OS metadata files, and reserved names (`integrity.json`,
   `integrity.sig`). The project's `plugin.json` is always the one packaged. Private dependencies
   (for example `ModularRuntime.Sdk.dll`, NuGet packages, native `runtimes/` assets) are included.
4. Validates the staged directory exactly as it will be installed (Loader rules, metadata, dependencies, paths).
5. Writes `integrity.json` (path, size, and SHA-256 of every file) with the Packaging library the
   daemon uses, and the ZIP with a fixed entry order and fixed timestamps. The same build
   output always produces the same bytes.
6. Prints the package path, file count, size, SHA-256, and the trust notice.

Default output: `bin\<Configuration>\modularruntime\<id>-<version>.zip`. An existing file is replaced
atomically. Ctrl+C stops the build and leaves no partial package.

### Local packages are not official

The package has **no** `integrity.sig`. The daemon installs it as *Local, unverified* only after
explicit acceptance. The hashes in `integrity.json` are declared by the packager: they prove that
the installed files are the packaged ones, and the daemon re-checks them before every load. They
do not prove who made the package or that anyone reviewed it. The tool has no access to official
keys and never creates a signature.

## inspect

Runs the daemon's own `PackageVerifier` on a copy in a temporary directory: archive structure
and limits, signature (when present) with the trusted keys, integrity manifest against the archive
entries, per-file size and SHA-256, `plugin.json`, API compatibility. It then reads the entry
assembly's metadata. It prints the plugin identity, API, entry, signature state, integrity
result, and every file with its size and hash. A signature by an unknown key is rejected and is
never treated as "unsigned". `--test-trust-store` adds test keys (for development registries only).

## Package layout

```text
<id>-<version>.zip
├── plugin.json
├── <Entry>.dll, <Entry>.deps.json, <Entry>.runtimeconfig.json
├── private dependencies (ModularRuntime.Sdk.dll, NuGet packages, runtimes/…)
└── integrity.json          (and integrity.sig only for official packages)
```

See [PLUGIN_PACKAGE_FORMAT.md](PLUGIN_PACKAGE_FORMAT.md) for the format and its limits.
