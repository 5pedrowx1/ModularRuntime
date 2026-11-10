# Plugin submission guide

Official plugins are built and signed by the ModularRuntime administrator from reviewed source code.
Developers submit **source**, not binaries to be signed.

## Publishing policy

1. The developer submits the complete source code.
2. The administrator reviews it.
3. The administrator builds it in a controlled environment.
4. The administrator computes the final hashes from that build.
5. The administrator approves it (a signed approval record bound to that build).
6. The administrator signs the package with an official key, offline.
7. The signed package is published.
8. The registry is updated and signed.

The developer tool has no part in steps 2–8 and no access to official keys. See
[PLUGIN_SUBMISSION_POLICY.md](PLUGIN_SUBMISSION_POLICY.md) and [PLUGIN_SIGNING.md](PLUGIN_SIGNING.md).

## Prepare a submission

```powershell
dotnet modularruntime prepare-submission --license MIT `
    --source-repository https://<your repository> --source-commit <full commit sha>   # required for review
dotnet modularruntime prepare-submission                    # in the plugin project folder (license/repository from the project if set)
dotnet modularruntime prepare-submission --source-root ..   # include the solution folder
dotnet modularruntime prepare-submission --no-package       # source only
```

The command validates the project and creates `submission/` (choose another folder with `-o`;
an existing non-empty folder is never overwritten):

```text
submission/
├── source/                 the project (or --source-root) without build output, IDE state, secrets, or keys
├── artifacts/              the local package built by `pack` (unless --no-package)
├── submission.json         schema 2: plugin ID, name, version, API, author, description, license, source
│                           repository and commit, project and solutions, lock files, dependencies, build
│                           instructions, .NET SDK and tool versions, excluded files
└── source-integrity.json   size and SHA-256 of every delivered file
```

**Excluded automatically** (and listed in `submission.json` under `excluded`):

- `bin/`, `obj/`, `.git/`, `.vs/`, `.idea/`, `.vscode/`, `node_modules/`, `packages/`, `TestResults/`, `artifacts/`
- keys, certificates, and credentials: `*.pfx`, `*.p12`, `*.pem`, `*.key`, `*.snk`, `*.jks`,
  `*.keystore`, `*.publishsettings`, `*.pubxml.user`, `*.kdbx`, `secrets.json`, `id_rsa*`
  and other SSH keys, `.env*`, `credentials`, `.npmrc`, `.pypirc`
- any file containing a PEM private key block, and `nuget.config` files with stored credentials
- IDE and user state: `*.user`, `*.suo`, `*.cache`, `*.swp`
- symbolic links and junctions (never followed)

Review the folder before sending it: the exclusions are a safety net, not a guarantee.

## What the hashes mean

`source-integrity.json` identifies exactly what you delivered, so you and the reviewers can refer
to the same bytes. **It does not prove the plugin is safe**, and the package in `artifacts/` is not
what gets published: the administrator rebuilds from the reviewed source and signs only that build.

The administrator validates the folder statically after you send it through ModularRuntime-Hub
(nothing is executed): the same files, hashes, identity, license, and lock-file rules described
above, plus a classification of build logic and risky APIs for the human review. Keep `packages.lock.json`
(the template enables `RestorePackagesWithLockFile`): the controlled build restores in locked mode.

## Before submitting

- `dotnet modularruntime validate --build` reports no errors.
- The plugin works installed from its local package (Install from ZIP): start, stop, start,
  restart, and remove.
- `plugin.json` has a meaningful `author` and `description`. The version is new for this plugin ID.
- The plugin uses only the public API and declares every dependency as a `PackageReference` or
  `ProjectReference` with source.
