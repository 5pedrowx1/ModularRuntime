# Official registry

The official registry is the catalog of plugins approved by the ModularRuntime administrator. It is a
plain directory, published from a GitHub repository controlled exclusively by the ModularRuntime administrator
(proposed name `ModularRuntime-Registry`), served over HTTPS (for example GitHub Pages or
release assets). Implementation: `src/ModularRuntime.Registry`.

```text
ModularRuntime-Registry/
├── registry.json            the catalog (signed bytes)
├── registry.sig             signature of registry.json
├── packages/                official ZIPs (or absolute https URLs in the entries)
├── README.md
├── schemas/
│   └── registry.schema.json
└── submissions/
    └── README.md            how to submit; submissions are reviewed, never merged directly
```

The public repository is the separate ModularRuntime-Registry project (`../ModularRuntime-Registry` in the local workspace); submissions never go there (they are received privately by ModularRuntime-Hub).

## Location

The daemon reads the location from, in order: `--registry <url|dir>`, `registryLocation` in
`<profile>\config\daemon.json`, and `DaemonOptions.DefaultRegistryLocation`, the built-in
default that the administrator sets before release (currently not set: "No official registry is
configured"). The location is a directory URL that contains `registry.json` and
`registry.sig`: `https://…/` for the official registry, or a local directory for mirrors and
offline tests. No repository name is hard-coded anywhere else.

## `registry.json`

Schema 2 (v0.6) adds, per entry, `status` (`published` when absent, `withdrawn`, `revoked`),
`statusReason` (required for `revoked`), `statusChangedAt`, `advisoryUrl`, `buildId`, and
`approvalSha256`, and at the top level `revokedKeys` (package signing keys revoked by the
administrator). Version 0.6 reads schemas 1 and 2. Withdrawn and revoked versions stay listed; the
"latest version" is the highest *published* one ([PACKAGE_REVOCATION.md](PACKAGE_REVOCATION.md)).

```json
{
  "schemaVersion": 1,
  "revision": 12,
  "generatedAt": "2026-11-08T00:00:00+00:00",
  "expiresAt": "2027-02-06T00:00:00+00:00",
  "plugins": [
    {
      "id": "com.example.filewatcher",
      "name": "File Watcher",
      "description": "File monitoring plugin.",
      "author": "Example Developer",
      "category": "Files",
      "version": "1.0.0",
      "apiVersion": "0.3",
      "minimumRuntimeVersion": "0.5.0",
      "publishedAt": "2026-11-08T00:00:00+00:00",
      "downloadUrl": "packages/com.example.filewatcher-1.0.0.zip",
      "packageSha256": "…64 lowercase hex…",
      "packageSize": 48211,
      "signingKeyId": "modularruntime-official-2026",
      "license": "MIT",
      "sourceRepository": "https://github.com/example/filewatcher",
      "sourceCommit": "4f1c2d9",
      "releaseNotes": "First release."
    }
  ]
}
```

| Field | Rule |
| --- | --- |
| `schemaVersion` | `1` |
| `revision` | Positive integer, increased with every publication (rollback protection) |
| `generatedAt` / `expiresAt` | `expiresAt` after `generatedAt`, at most 400 days later |
| `id` | Valid plugin ID; one entry per `id` + `version` |
| `name` / `author` | 1–200 characters, no control characters |
| `description` | 1–1000 characters |
| `category` | 1–50 letters, digits, spaces, `&` or `-` |
| `version` | 2–4 numeric components |
| `apiVersion` | Plugin API `major.minor` |
| `minimumRuntimeVersion` | Lowest ModularRuntime version allowed to install it |
| `downloadUrl` | Absolute `https` URL (no user info), or a relative path of simple segments (no `..`) |
| `packageSha256` / `packageSize` | Of the ZIP; the download is limited to `packageSize` bytes |
| `signingKeyId` | Key that signed the package's `integrity.json`; must match the package |
| `license`, `sourceRepository` (https), `sourceCommit` (hex), `releaseNotes` | Optional |

Unknown or duplicate properties, missing required fields, invalid values, and duplicate
entries reject the whole catalog. Several versions of a plugin may be listed; clients use the
highest compatible one.

## Authenticity

1. `registry.json` and `registry.sig` are downloaded (size limits 8 MiB and 16 KiB).
2. The signature is verified over the exact bytes with a key trusted for **registry** signing
   ([PLUGIN_SIGNING.md](PLUGIN_SIGNING.md)). Nothing in the catalog is used before this.
3. The document is parsed and validated.
4. Freshness: `expiresAt` must be in the future; `generatedAt` may not be more than 24 hours
   in the future (clock skew); `revision` must be ≥ the highest revision this profile has
   accepted, and the same revision must have the same content.
5. The accepted catalog and signature are cached in `<profile>\packages\registry\` and the
   highest revision and its SHA-256 in `state.json`.

HTTPS, the repository name, and the hash of a package are never trusted on their own: the
package hash comes from the signed catalog, and each package carries its own signature.

### Rollback and replay limits

- A profile never goes back to an older revision once it accepted a newer one
  (`registry_rollback`), and rejects a different catalog with the same revision
  (`registry_conflict`).
- A new installation (no state) or a profile that stays offline accepts any validly signed,
  unexpired catalog. An attacker who controls the network can therefore replay an older signed
  catalog to a fresh installation, or withhold newer ones, until that catalog expires. They
  cannot forge entries or packages. Keep `expiresAt` short enough (90 days is the publisher
  default in the docs) and publish a new revision before expiry.
- Deleting `state.json` resets the rollback floor for that profile; it is in the user's own
  profile, inside the same trust boundary as everything else the daemon owns.

## Offline use

The cached catalog is re-verified at every daemon start and used offline until it expires. Its
revocations stay in force even after it expires (an authentic revocation remains true), until a
newer verified revision replaces it.
Official packages already installed never need the registry: they are verified from their own
signed manifests. An official package can also be installed offline from a local ZIP: its
signature is verified without the catalog (there is no online revocation check; see
[PLUGIN_SIGNING.md](PLUGIN_SIGNING.md)).

## Updates

`packages.checkUpdates` compares installed managed packages with the highest version in the
cached catalog. Updates are only applied on an explicit request (GUI *Update*, CLI
`plugins update`); there is no automatic update.

## Publishing a revision

A revision is produced and signed offline by the administrator and published by committing the new
`registry.json`, `registry.sig`, and packages through a reviewed pull request in the registry
repository. The publication rules are public code (`RegistryCatalogCheck` in
`ModularRuntime.Registry`) and are checked in the registry's CI and by anyone with
`dotnet modularruntime registry-check`: published versions are immutable (same package SHA-256,
size, key, URL, API, and minimum runtime), never dropped silently, revocations are kept, and new
versions must target the supported plugin API. Automation never signs or publishes by itself.
