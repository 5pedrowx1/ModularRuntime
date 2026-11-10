# Plugin submission policy

Only the ModularRuntime administrator approves, builds, signs, and publishes official plugins. Other
developers can submit plugins for review; they cannot change the published registry.

Since v0.6 this policy is enforced by the administrator's tools: a static submission validator, a
recorded security review, controlled builds, and approvals signed with the administrator's
approvals key and bound to the exact build output. **Every new version needs a new review and a
new approval.**

## What a submission contains

- the complete source code, `.csproj` files, the solution, and any build tooling;
- `plugin.json`;
- declared dependencies with versions, and lock files (`packages.lock.json`) when NuGet
  packages are used;
- build instructions (SDK version, configuration, commands);
- license (SPDX expression, or `LicenseRef-…` with its text), the public https source repository,
  and the exact commit (`submission.json` schema 2: `license`, `sourceRepository`, `sourceCommit`);
- README;
- compiled binaries, when the author provides them (for comparison only);
- a list of the SHA-256 of every submitted file.

The author's hashes verify that the material arrived unchanged. **They are not evidence of
safety, and not evidence that the binaries match the source.**

Submissions arrive as an issue or pull request to `submissions/` in the registry repository
with a link to an immutable source snapshot (a tag or commit). Nothing from a submission is
merged into the published catalog by the submitter.

## Review (human review is mandatory)

1. Record the submission, its source snapshot, and its hashes.
2. Validate the structure (manifest, project files, licenses).
3. Read the source code. Look for: process execution, file access outside the plugin's
   purpose, network access, native code (P/Invoke, unmanaged libraries), reflection over the
   host or other plugins, dynamic code loading (`Assembly.Load*`, `AssemblyLoadContext`),
   obfuscation, embedded binaries, credentials, and telemetry.
4. Inspect dependencies: provenance, version, license, known vulnerabilities
   (`dotnet list package --vulnerable`), and anything vendored as binaries.
5. Check build configuration: custom MSBuild targets, pre/post-build steps, analyzers or
   source generators that run code at build time.
6. Static analysis (the .NET analyzers at `latest-recommended`, plus any security scanner the
   administrator adopts).
7. Build in a controlled, isolated, preferably ephemeral environment (below).
8. Run the plugin's tests and a load test against the current runtime.
9. Assess behavior: what the plugin accesses while running, observed in an isolated machine.
10. Compare the submitted binaries with the controlled build, when technically possible.
11. Approve or reject the version, recording the reason.

Automation may help (checks, builds, scans) but never approves arbitrary code on its own. Code
from submissions never runs on the machine that holds signing keys.

## Controlled build

Official packages are built by the administrator **from the approved source**; submitted binaries are
never published.

- Build on a clean, ephemeral machine or container without access to signing keys, with
  network access limited to the package feeds needed for restore.
- Pin the SDK (`global.json`) and restore with lock files (`--locked-mode`).
- Record: source commit or snapshot and its SHA-256, SDK version, dependency versions and
  hashes, build parameters, test results, and the SHA-256 of every artifact (the integrity
  manifest that is signed lists every file).
- Prefer deterministic builds (`Deterministic=true`, `ContinuousIntegrationBuild=true`,
  source paths mapped). When the result is reproducible, a second independent build must
  produce identical hashes. When it is not, record why (for example timestamps or
  non-deterministic generators) and rely on the recorded build of the approved snapshot.
- After approval the artifacts are immutable: any change requires a new version and review.

## Approval record

An approval is a record signed with the administrator's personal approvals key. It names the
plugin, the version, the reviewed source, the controlled build, and the SHA-256 of the integrity
manifest that will be signed. The official signing tool refuses to sign anything that does not
match an approval exactly: any change to an artifact after approval changes its SHA-256 and blocks
signing.

## Publication

1. The package is signed on the offline signing machine (the key never leaves it).
2. The signed package is verified with the production trust store.
3. The next catalog revision is built, signed, and verified.
4. The owner opens a pull request in the registry repository with the package, `registry.json`,
   and `registry.sig`, reviews it with the pull request checklist, and merges after the required
   checks pass (`dotnet modularruntime registry-check`). Automation never holds keys and never
   publishes on its own.

## Removing a published version

Since v0.6 a published version is never removed: it is **withdrawn** (not offered; installed
copies keep working) or **revoked** (never installed, installed copies never loaded again) in a
signed catalog revision, and a compromised packages key can be revoked by the catalog too
([PACKAGE_REVOCATION.md](PACKAGE_REVOCATION.md)).
