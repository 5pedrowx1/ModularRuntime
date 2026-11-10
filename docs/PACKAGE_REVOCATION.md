# Package revocation and withdrawal

Four situations are kept apart:

| Situation | Catalog | Store | Installed copies |
|---|---|---|---|
| **Withdrawn** version (publishing mistake, end of support, incompatible API) | `status: withdrawn`, `statusReason` | not offered; listed in the version history | keep working; the Plugins page shows "withdrawn from the Store" |
| **Revoked** version (malicious package, serious vulnerability, compromised integrity or provenance) | `status: revoked`, `statusReason` (required), `advisoryUrl` | not installable; shown as "Revoked for security" | **not loaded again**; shown as Revoked with the reason; not deleted |
| **Revoked signing key** | `revokedKeys` (and the product trust store) | every package signed by it is not installable | not loaded again |
| **Local, unverified** package | not in the catalog | — | the catalog cannot speak for it, **except** when its exact package (same ID, version, and package SHA-256) is revoked: then it is refused and not loaded either |

## Publishing a revocation

A withdrawal or revocation is a new signed catalog revision prepared by the administrator: the
entry gets `status` (`withdrawn` or `revoked`), a reason, and optionally an advisory URL. The entry
stays in the catalog with its SHA-256, so the history remains auditable and the daemon can match
installations. A revoked version stays revoked in later revisions unless it is explicitly
reinstated after an incident review (`dotnet modularruntime registry-check --previous` rejects a
silent reinstatement).

## Daemon behavior

- The revocation source is always a catalog whose signature verified with a registry key. An
  unsigned or differently signed catalog is rejected as a whole; nothing in it is applied.
- Before every load, the daemon's load gate (`RevocationGate`, before the integrity check)
  refuses an installation whose version and package SHA-256 are revoked, or whose signing key
  is revoked (`installation_revoked`).
- **Running plugins:** a revocation does not stop a running plugin automatically (stopping it
  abruptly can lose data, and the plugin already ran with the user's permissions). The daemon
  logs a warning, the desktop app shows it, and the plugin is never loaded again after it stops.
- **Installations are kept** so the user can inspect and remove them; nothing is deleted silently.
- **Offline:** revocations of the last verified catalog stay in force across restarts and even
  after that catalog expires (an authentic revocation never becomes less true), but an offline
  daemon learns about new revocations only when it can refresh. Revocation is therefore not
  instantaneous.
- **Updates and reinstallation:** a revoked version cannot be installed from the Store or as a
  local ZIP; an update must be a newer, published version, verified like a new package.
