# Repository organization

This repository is the public ModularRuntime source: the runtime, the plugin SDK, the developer
tool, and everything needed to verify official packages and the official catalog. It builds and
tests on its own and references no other repository. Nothing was committed, tagged, or pushed.

The ModularRuntime administrator's review, approval, signing, and publication tools are kept in a
separate private repository that depends on this one (never the other way round). Their security
does not rely on being private: it rests on offline keys, signed approvals, and the public
verification in this repository. The official catalog has its own public repository
(ModularRuntime-Registry); plugin submissions are received privately (ModularRuntime-Hub).

## Projects

| Project | Role | Shipped to |
|---|---|---|
| Contracts | plugin API | NuGet (SDK) + app |
| Engine | lifecycle, events, JSON/binary/typed services, streams, extensions | NuGet (SDK) + app |
| Loader | discovery, manifests, collectible load contexts, shared contract contexts | NuGet (SDK) + app |
| Sdk | helpers for plugin authors | NuGet (SDK) |
| Templates | `dotnet new modularruntime-plugin` | NuGet (SDK) |
| DevTools | `dotnet modularruntime`: validate, pack, inspect, prepare-submission, registry-check | NuGet tool (SDK) |
| Submissions | public submission format (`submission.json` schema 2, `source-integrity.json`) and secret classifier | inside DevTools |
| Ipc.Contracts, Ipc | client/daemon protocol | app |
| Daemon, Cli, Desktop | the application (`scripts/publish.ps1`) | app |
| Packaging | package format, signatures, trust store, verification, installation | app, DevTools |
| Registry | signed catalog client, revocation source, public publication rules (`RegistryCatalogCheck`) | app, DevTools |

No two projects duplicate a responsibility: Contracts is the plugin API and Ipc.Contracts the
client/daemon wire protocol; Packaging verifies packages and Registry verifies catalogs. The
signature envelope writer (`SignatureEnvelope.Sign`) stays next to the reader: it is the public
format's counterpart of verification, used by tests with temporary TEST keys; production keys and
the tools that use them are not here.

| Folder | Notes |
|---|---|
| `samples/` | example plugins (including a shared contract assembly) and an embedded host, used by tests and docs |
| `scripts/` | `publish`, `pack-sdk`, `prepare-plugins` |
| `tests/` | the full suite; `RepositoryHygieneTests` keep keys, publishing records, and administrative code out |
| `docs/` | user, author, and architecture documentation; `CHANGELOG.md` keeps the per-version history |
| `benchmarks/` | BenchmarkDotNet suite of the communication mechanisms (not part of the tests) |
| `artifacts/`, `plugins/*` | build output and generated staging (ignored); `artifacts/test-registry` is a local development catalog signed with TEST keys |

## What is distributed

- **Users:** the application (`ModularRuntime.Daemon`, `ModularRuntime.Cli`,
  `ModularRuntime.Desktop`, with Contracts, Engine, Loader, Ipc, Packaging, Registry).
- **Plugin authors:** the SDK as NuGet packages (Contracts, Sdk, Engine, Loader, Templates,
  DevTools).
- **Not distributed and not in this repository:** the administrator's publishing tools.

## `.gitignore` and `.gitattributes`

The root `.gitignore` excludes build output, IDE state, keys and credentials, and publishing working
folders (`src/**/OfficialTrustStore.json` stays tracked: it holds public keys only). `.gitattributes`
normalizes text and keeps binaries untouched.
