# Plugin and registry signing

## Algorithm and format

- **ECDSA on NIST P-256 with SHA-256**, through the .NET 10 `System.Security.Cryptography.ECDsa`
  API (Windows CNG).
- Signatures are **DER-encoded** (`DSASignatureFormat.Rfc3279DerSequence`, the RFC 3279
  `Ecdsa-Sig-Value` sequence), base64 in the signature file.
- What is signed: the **exact bytes** of `integrity.json` (packages) or `registry.json` (the
  registry). Signers and verifiers never re-serialize.
- Signature file (`integrity.sig`, `registry.sig`), UTF-8 JSON:

```json
{
  "format": "modularruntime-signature/1",
  "algorithm": "ECDSA-P256-SHA256",
  "keyId": "modularruntime-official-2026",
  "signature": "MEUCIQ…"
}
```

The `keyId` only selects which trusted key verifies the signature. A signature never brings
its own key: a key that is not in the trust store is rejected (`unknown_signing_key`), and a
signature made with another key under a trusted ID does not verify (`signature_invalid`).

## Key purposes (v0.6)

| Purpose | Signs | Trusted by |
|---|---|---|
| `registry` | `registry.json` | daemons, CI |
| `packages` | `integrity.json` | daemons, CI |
| `approvals` | the administrator's approval records | the administrator's private publishing tools only |

Production keys have exactly one purpose; the official signing tools check that a private key
matches the named trusted key, has the right purpose, and is not revoked before they sign.

## Trust store

Public keys only, by key ID:

```json
{
  "schemaVersion": 1,
  "keys": [
    {
      "keyId": "modularruntime-official-2026",
      "algorithm": "ECDSA-P256-SHA256",
      "publicKey": "base64 SubjectPublicKeyInfo",
      "purposes": ["packages", "registry"],
      "status": "active",
      "test": false
    }
  ]
}
```

- **Official keys** are compiled into `ModularRuntime.Packaging`
  (`src/ModularRuntime.Packaging/OfficialTrustStore.json`, an embedded resource). It is
  **empty in this pre-release**: no production key exists yet, by design. Before the first
  release the administrator generates the production key offline and adds its public key here. Test
  keys are refused in this file.
- **Purposes** limit a key to package signing, registry signing, or both. Separate keys are
  recommended for production: compromising the registry key then does not allow forging
  packages, and vice versa.
- **Test keys** (`"test": true`) are only accepted from a file passed explicitly to the daemon
  with `--test-trust-store` (development and automated tests). The daemon logs a warning, and
  every client labels such packages "Official (test key)". Configuration files, IPC clients,
  the registry, and packages cannot add keys.

### Rotation and revocation

- **Rotation:** ship a product update that adds the new key (`active`) while the old one stays
  `active`; start signing with the new key; once every published package and registry revision
  has been re-signed, ship an update that marks the old key `revoked` (or removes it).
  Installed packages are re-verified before every load, so a revoked key also stops already
  installed packages signed with it until they are reinstalled from re-signed packages.
- **Compromise:** mark the key `revoked` in an update, re-sign everything with a new key,
  publish a new registry revision. There is no online revocation list: revocation reaches a
  machine with the product update.

## Key handling rules

- The private key is **never** in the repository, in any build output, in a test, in a log, in
  IPC, or on a developer machine. The daemon cannot sign anything; it only holds public keys.
- The administrator's key generation refuses to write a private key inside a Git working tree
  and never overwrites a key.
- Production keys: generated on an offline, dedicated signing machine (or an HSM), encrypted
  with a strong passphrase that never appears on a command line, backed up offline in two
  places, accessible only to the administrator, and every signing operation is recorded.
- Signing happens only on the signing machine, on a prepared package that has an approval
  record ([PLUGIN_SUBMISSION_POLICY.md](PLUGIN_SUBMISSION_POLICY.md)); building and testing
  happen elsewhere and never see the key.

## Producing and verifying signatures

Official signatures are produced only by the ModularRuntime administrator, offline, with tools
that are not part of this repository; they hold no secret beyond the keys, and knowing them does
not help forge a signature. Everything needed to **verify** is public:

- `dotnet modularruntime inspect <package.zip>` verifies a package exactly like the daemon
  (integrity manifest, signature, key purpose, revocation).
- `dotnet modularruntime registry-check <registry.json>` verifies a catalog revision: signature by
  a trusted registry key, validity window, revision greater than the previous one
  (`--previous`), published versions unchanged, revocations kept, and package files matching their
  entries (`--packages-root`). The public registry's CI runs the same command.
- Tests create temporary TEST keys in memory (`ModularRuntime.TestSupport`); test keys are trusted
  only through an explicit `--test-trust-store`.
