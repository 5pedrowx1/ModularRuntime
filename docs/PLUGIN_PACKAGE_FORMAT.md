# Plugin package format (v1)

A plugin package is a ZIP file containing one plugin installation plus, for official packages,
a signed list of every file. Implementation: `src/ModularRuntime.Packaging`
(`PackageArchive`, `PackagePaths`, `IntegrityManifest`, `PackageVerifier`, `PackageBuilder`).

```text
FileWatcher-1.0.0.zip
├── plugin.json                 content: the plugin manifest (see PLUGIN_DEVELOPMENT.md)
├── FileWatcher.dll             content
├── FileWatcher.deps.json       content
├── README.md                   content
├── dependencies/
│   └── ExampleLibrary.dll      content
├── integrity.json              metadata: SHA-256 and size of every content file
└── integrity.sig               metadata: signature of the exact bytes of integrity.json
```

## Content and metadata

- **Content files** are everything that is installed. Every content file must be listed in
  `integrity.json` exactly once, with its size and SHA-256. That includes `plugin.json`, every
  DLL, every dependency, and any data file.
- **Metadata files** are exactly `integrity.json` and `integrity.sig` **at the package root**,
  in lowercase. They are not installed as content and are never listed in `integrity.json`:
  `integrity.json` is protected by the signature in `integrity.sig`, and the signature cannot
  cover itself. The same names in another case at the root are rejected. A file with one of
  these names in a subdirectory (for example `docs/integrity.json`) is ordinary content.
- An **official package** has both metadata files, and the signature verifies with a trusted
  official key ([PLUGIN_SIGNING.md](PLUGIN_SIGNING.md)).
- An **unsigned package** has no `integrity.sig`. It may carry an author-written
  `integrity.json`; if it does, the file must be consistent with the content (otherwise the
  package is rejected), but it proves nothing about who made the package. When it is absent,
  the daemon generates the inventory itself at installation.
- A package with `integrity.sig` whose signature does not verify, or whose key is unknown,
  revoked, or not allowed to sign packages, is **rejected**. It is never downgraded to
  "unsigned".

## `integrity.json`

```json
{
  "schemaVersion": 1,
  "pluginId": "com.example.hello",
  "version": "1.0.0",
  "hashAlgorithm": "SHA-256",
  "files": [
    { "path": "HelloPlugin.deps.json", "size": 862, "sha256": "…64 lowercase hex…" },
    { "path": "HelloPlugin.dll", "size": 6144, "sha256": "…" },
    { "path": "plugin.json", "size": 210, "sha256": "…" }
  ]
}
```

| Field | Rule |
| --- | --- |
| `schemaVersion` | `1` |
| `pluginId` | A valid plugin ID; must equal the ID in `plugin.json` and, for registry installs, the registry entry |
| `version` | 2–4 numeric components; must equal `plugin.json` (and the registry entry) |
| `hashAlgorithm` | `SHA-256` |
| `files` | Non-empty; must contain `plugin.json`; each path at most once (case-insensitive) |
| `path` | A package path (rules below) |
| `size` | Bytes, `0` to the file limit |
| `sha256` | 64 lowercase hexadecimal characters |

Strict parsing: unknown properties, duplicate properties, a byte-order mark, and listed
metadata files are rejected.

**Determinism.** The publisher writes the manifest with UTF-8 without BOM, two-space
indentation, LF line endings, no trailing newline, properties in the order above, and files
sorted by ordinal comparison of `path`. The same set of files always produces the same bytes.
Verification never re-serializes: the signature is checked over the bytes found in the
package, then those bytes are parsed.

## Package paths

Paths in the archive and in `integrity.json` are the same strings:

- separator `/` only; a backslash is rejected;
- relative: no leading `/`, no drive (`C:`), no UNC or device prefix (`//server`, `\\?\`);
- no `:` anywhere (this also rules out alternate data streams such as `file.dll:stream`);
- segments: not empty, not `.` or `..`, no control characters, none of `< > : " | ? * \`, not
  ending with `.` or a space, not a DOS device name (`CON`, `PRN`, `AUX`, `NUL`, `COM0`–`COM9`,
  `LPT0`–`LPT9`, `CONIN$`, `CONOUT$`, with or without an extension);
- Unicode normalization form C;
- at most 200 characters and 16 levels deep (configurable);
- two paths are the same file when they are equal ignoring case (ordinal upper-invariant
  comparison), so `A.dll` and `a.dll`, or a file `lib` and a directory `lib/`, collide.

## Archive rules and limits

Defaults (`PackageLimits`), all configurable:

| Limit | Default |
| --- | --- |
| Package file size | 256 MiB |
| Archive entries | 4096 |
| One file, decompressed | 128 MiB |
| All files, decompressed | 512 MiB |
| Compression ratio of an entry over 1 MiB | 200:1 |
| Path length / depth | 200 characters / 16 levels |
| `integrity.json` / `integrity.sig` | 4 MiB / 16 KiB |

Rejected entries: symbolic links (Unix mode type other than regular file or directory),
Windows reparse points (attribute `0x400`), directory entries with content, duplicate names,
case-insensitive collisions, and file/directory collisions. Encrypted entries and unsupported
compression methods make the archive invalid.

Sizes are enforced twice: on the sizes declared by the archive before extraction, and on the
bytes actually produced while decompressing (a file that expands beyond its declared size or the
limits is rejected mid-stream). Extraction creates only new files (`FileMode.CreateNew`) in an
empty, daemon-owned directory, with every destination resolved and checked to stay inside it.
Nothing is ever extracted with a generic "extract all" call.

## Verification order

`PackageVerifier.VerifyAndExtractAsync` (used by the daemon for every installation):

1. SHA-256 of the staged package file; compared with the signed registry value for registry installs.
2. Archive structure and limits.
3. `integrity.sig` verified over the exact `integrity.json` bytes with a trusted key
   (and, for registry installs, the key named by the registry entry).
4. `integrity.json` parsed; plugin ID and version compared; the set of archive entries must
   equal the listed files (sizes included).
5. Extraction into staging with real limits; every file's size and SHA-256 compared.
6. `plugin.json` validated by the Loader's own reader (JSON only; nothing is loaded); its ID
   and version must match the manifest.
7. Plugin API compatibility.

No assembly is loaded and no plugin code runs during any of these steps.

## Error codes

| Code | Meaning |
| --- | --- |
| `package_too_large` | The package file exceeds the size limit |
| `package_corrupt` | Not a valid ZIP, truncated, undecompressable, or size mismatch |
| `unsafe_entry_path` | A path breaks the rules above |
| `duplicate_entry` | Duplicate or colliding paths |
| `unsupported_entry_type` | Symbolic link, reparse point, or special file |
| `too_many_entries` | More entries than allowed |
| `entry_too_large` | A file larger than allowed |
| `expansion_limit_exceeded` | Decompression or total size limits, suspicious ratio |
| `integrity_manifest_invalid` | `integrity.json` fails the schema |
| `signature_invalid` | Bad signature file or signature does not verify |
| `signature_missing` | An official signature was required |
| `unknown_signing_key` | Key not trusted (or not for this purpose) |
| `signing_key_revoked` | Key marked revoked |
| `integrity_mismatch` | A file is missing, extra, or has another size or SHA-256 |
| `identity_mismatch` | Plugin ID, version, or signing key differs from what was expected |
| `plugin_manifest_invalid` | `plugin.json` fails the Loader's rules |
| `api_version_incompatible` | The plugin targets another plugin API |
| `runtime_version_incompatible` | The registry entry needs a newer ModularRuntime |
| `package_hash_mismatch` | The package file differs from the signed registry value |
| `installation_conflict` | A hand-copied folder already uses the plugin ID |
| `files_in_use` | The installation could not be moved (files locked) |
