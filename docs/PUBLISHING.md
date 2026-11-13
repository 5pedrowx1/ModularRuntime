# Official publishing: submission, review, controlled build, signing, revocation

Official plugins are built and signed by the ModularRuntime administrator from reviewed source
code. Developers submit **source**, not binaries to be signed. Only the administrator approves,
builds, signs, and publishes; other developers cannot change the published registry.

1. The developer submits the complete source code.
2. The administrator reviews it.
3. The administrator builds it in a controlled environment.
4. The administrator computes the final hashes from that build.
5. The administrator approves it (a signed approval record bound to that build).
6. The administrator signs the package with an official key, offline.
7. The signed package is published.
8. The registry is updated and signed.

The developer tool has no part in steps 2–8 and no access to official keys. The administrator's
tools live in a separate private repository that depends on this one; the formats and every
verification are public here ([PLUGIN_SIGNING.md](PLUGIN_SIGNING.md),
[OFFICIAL_REGISTRY.md](OFFICIAL_REGISTRY.md), [PLUGIN_PACKAGE_FORMAT.md](PLUGIN_PACKAGE_FORMAT.md)).
Every new version needs a new review and a new approval.

## Prepare a submission

```powershell
dotnet modularruntime prepare-submission --license MIT `
    --source-repository https://<your repository> --source-commit <full commit sha>   # required for review
dotnet modularruntime prepare-submission                    # license/repository from the project if set
dotnet modularruntime prepare-submission --source-root ..   # include the solution folder
dotnet modularruntime prepare-submission --no-package       # source only
```

The command validates the project and creates `submission/` (`-o` for another folder; an existing
non-empty folder is never overwritten):

```text
submission/
├── source/                 the project (or --source-root) without build output, IDE state, secrets, or keys
├── artifacts/              the local package built by `pack` (unless --no-package)
├── submission.json         schema 2: plugin ID, name, version, API, author, description, license, source
│                           repository and commit, project and solutions, lock files, dependencies, build
│                           instructions, .NET SDK and tool versions, excluded files
└── source-integrity.json   size and SHA-256 of every delivered file
```

Excluded automatically (listed under `excluded`): `bin/`, `obj/`, `.git/`, `.vs/`, `.idea/`,
`.vscode/`, `node_modules/`, `packages/`, `TestResults/`, `artifacts/`; keys, certificates, and
credentials (`*.pfx`, `*.p12`, `*.pem`, `*.key`, `*.snk`, `*.jks`, `*.keystore`,
`*.publishsettings`, `*.pubxml.user`, `*.kdbx`, `secrets.json`, `id_rsa*` and other SSH keys,
`.env*`, `credentials`, `.npmrc`, `.pypirc`); any file containing a PEM private key block and
`nuget.config` files with stored credentials; IDE and user state (`*.user`, `*.suo`, `*.cache`,
`*.swp`); symbolic links and junctions (never followed). Review the folder before sending it: the
exclusions are a safety net, not a guarantee.

What the hashes mean: `source-integrity.json` identifies exactly what you delivered, so you and
the reviewers refer to the same bytes. It does not prove the plugin is safe, and the package in
`artifacts/` is not what gets published: the administrator rebuilds from the reviewed source and
signs only that build.

Before submitting:

- `dotnet modularruntime validate --build` reports no errors;
- the plugin works installed from its local package (Install from ZIP): start, stop, start,
  restart, remove;
- `plugin.json` has a meaningful `author` and `description`; the version is new for this plugin ID;
- the plugin uses only the public API and declares every dependency as a `PackageReference` or
  `ProjectReference` with source; keep `packages.lock.json` (the template enables it): the
  controlled build restores in locked mode;
- shared contract assemblies (`sharedContracts`) are part of the submitted source and follow the
  contract rules ([COMMUNICATION.md](COMMUNICATION.md#shared-contract-assemblies)); the validator
  reports a contract with private dependencies.

Submissions are received through ModularRuntime-Hub, which validates the folder statically
(nothing is executed): the same files, hashes, identity, license, and lock-file rules, plus a
classification of build logic and risky APIs for the human review.

## What a submission contains

The complete source code, project files, solution, and build tooling; `plugin.json`; declared
dependencies with versions and lock files; build instructions (SDK version, configuration,
commands); the license (SPDX expression or `LicenseRef-…` with its text), the public https source
repository, and the exact commit; a README; compiled binaries when the author provides them (for
comparison only); the SHA-256 of every submitted file. The author's hashes verify that the
material arrived unchanged; they are not evidence of safety or that binaries match the source.

## Review (human review is mandatory)

1. Record the submission, its source snapshot, and its hashes.
2. Validate the structure (manifest, project files, licenses).
3. Read the source code: process execution, file access outside the plugin's purpose, network
   access, native code (P/Invoke, unmanaged libraries), reflection over the host or other plugins,
   dynamic code loading (`Assembly.Load*`, `AssemblyLoadContext`), obfuscation, embedded binaries,
   credentials, telemetry; for typed services, what the contract exposes and what crosses it.
4. Inspect dependencies: provenance, version, license, known vulnerabilities
   (`dotnet list package --vulnerable`), vendored binaries.
5. Check build configuration: custom MSBuild targets, pre/post-build steps, analyzers or source
   generators that run code at build time.
6. Static analysis (the .NET analyzers at `latest-recommended`, plus any adopted scanner).
7. Build in a controlled, isolated, preferably ephemeral environment (below).
8. Run the plugin's tests and a load test against the current runtime.
9. Assess behavior: what the plugin accesses while running, observed on an isolated machine.
10. Compare the submitted binaries with the controlled build, when technically possible.
11. Approve or reject the version, recording the reason.

Automation may help (checks, builds, scans) but never approves arbitrary code on its own. Code
from submissions never runs on the machine that holds signing keys.

## Controlled build

Official packages are built by the administrator from the approved source; submitted binaries are
never published.

- A clean, ephemeral machine or container without access to signing keys, with network access
  limited to the package feeds needed for restore.
- The SDK pinned (`global.json`), restore with lock files (`--locked-mode`).
- Recorded: source commit or snapshot and its SHA-256, SDK version, dependency versions and
  hashes, build parameters, test results, and the SHA-256 of every artifact (the integrity
  manifest that is signed lists every file).
- Deterministic builds preferred (`Deterministic=true`, `ContinuousIntegrationBuild=true`, source
  paths mapped); when reproducible, a second independent build must produce identical hashes;
  otherwise the reason is recorded and the recorded build of the approved snapshot is relied on.
- After approval the artifacts are immutable: any change requires a new version and review.

## Approval and signing

An approval is a record signed with the administrator's personal **approvals** key (a key
purpose the daemon never trusts). It names the plugin, the version, the reviewed source, the
controlled build, and the SHA-256 of the integrity manifest that will be signed. The official
signing tool refuses to sign anything that does not match an approval exactly: any change to an
artifact after approval changes its SHA-256 and blocks signing.

1. The package is signed on the offline signing machine (the key never leaves it).
2. The signed package is verified with the production trust store.
3. The next catalog revision is built, signed, and verified.
4. The administrator opens a pull request in the registry repository with the package,
   `registry.json`, and `registry.sig`, reviews it with the pull request checklist, and merges
   after the required checks pass (`dotnet modularruntime registry-check`). Automation never holds
   keys and never publishes on its own.

## Withdrawal and revocation

A published version is never removed. Four situations are kept apart:

| Situation | Catalog | Store | Installed copies |
|---|---|---|---|
| **Withdrawn** version (publishing mistake, end of support, incompatible API) | `status: withdrawn`, `statusReason` | not offered; listed in the version history | keep working; the Plugins page shows "withdrawn from the Store" |
| **Revoked** version (malicious package, serious vulnerability, compromised integrity or provenance) | `status: revoked`, `statusReason` (required), `advisoryUrl` | not installable; shown as "Revoked for security" | **not loaded again**; shown as Revoked with the reason; not deleted |
| **Revoked signing key** | `revokedKeys` (and the product trust store) | every package signed by it is not installable | not loaded again |
| **Local, unverified** package | not in the catalog | — | the catalog cannot speak for it, except when its exact package (same ID, version, and package SHA-256) is revoked: then it is refused and not loaded either |

A withdrawal or revocation is a new signed catalog revision prepared by the administrator; the
entry stays in the catalog with its SHA-256, so the history remains auditable and the daemon can
match installations. A revoked version stays revoked in later revisions unless explicitly
reinstated after an incident review (`registry-check --previous` rejects a silent reinstatement).

Daemon behavior:

- the revocation source is always a catalog whose signature verified with a registry key; an
  unsigned or differently signed catalog is rejected as a whole;
- before every load, the load gate (`RevocationGate`, before the integrity check) refuses an
  installation whose version and package SHA-256 are revoked, or whose signing key is revoked
  (`installation_revoked`);
- a running plugin is **not** stopped automatically (stopping it abruptly can lose data, and it
  already ran with the user's permissions): the daemon logs a warning, the desktop app shows it,
  and the plugin is never loaded again after it stops; installations are kept for inspection;
- revocations of the last verified catalog stay in force across restarts and after it expires;
  an offline daemon learns about new revocations only when it can refresh, so revocation is not
  instantaneous;
- a revoked version cannot be installed from the Store or as a local ZIP; an update must be a
  newer, published version, verified like a new package.
