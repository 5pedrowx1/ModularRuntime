# Getting started: your first plugin

Every command on this page is exercised by the end-to-end tests in
`tests/ModularRuntime.Template.Tests` (outside the repository, from the packed SDK).

## 1. Install the .NET 10 SDK

Install the .NET 10 SDK from https://dotnet.microsoft.com/download and check it:

```powershell
dotnet --list-sdks          # a 10.0.x entry
```

## 2. Install the templates and the developer tool

ModularRuntime is pre-release and its packages are not on nuget.org. Build the local packages
from a ModularRuntime checkout, or use the folder you received:

```powershell
./scripts/pack-sdk.ps1                       # writes artifacts\nuget\*.nupkg (version 0.6.0)
$feed = (Resolve-Path ./artifacts/nuget).Path

dotnet new install "$feed\ModularRuntime.Templates.0.6.0.nupkg"
dotnet tool install --global ModularRuntime.DevTools --version 0.6.0 --add-source $feed
```

Your plugin projects restore `ModularRuntime.Sdk` from that folder. Add it as a NuGet source once:
`dotnet nuget add source $feed --name modularruntime-local`, or put a `nuget.config` next to
your projects.

## 3. Create a project

```powershell
dotnet new modularruntime-plugin -n MyFirstPlugin
cd MyFirstPlugin
```

```text
MyFirstPlugin/
├── MyFirstPlugin.csproj   net10.0, EnableDynamicLoading, PackageReference ModularRuntime.Sdk 0.6.0
├── MyFirstPlugin.cs       the entry type (IPlugin)
├── plugin.json            id com.example.myfirstplugin, apiVersion 0.3, entry assembly and type
├── README.md
└── .gitignore
```

Options: `--kind basic|event-publisher|event-subscriber|service-provider|service-consumer|extension`,
`--plugin-id com.contoso.reports` (default `com.example.<project name>`), `--plugin-name`,
`--plugin-author`, `--topic`, `--service-id`, `--extension-point`. See `dotnet new modularruntime-plugin -h`.

## 4. Build

```powershell
dotnet build
```

## 5. Validate

```powershell
dotnet modularruntime validate --build
```

`validate` checks the project and `plugin.json` with the same rules as the runtime (ID, version,
API version, entry assembly and type, target framework, references, dependencies, path rules).
It reads the built assembly as metadata only: no plugin code runs. Errors have stable codes
([PLUGIN_PACKAGING.md](PLUGIN_PACKAGING.md#diagnostics)).

## 6. Pack

```powershell
dotnet modularruntime pack
```

The tool builds with `dotnet build -c Release` and writes
`bin\Release\modularruntime\com.example.myfirstplugin-1.0.0.zip`. The package holds your plugin's
files, `plugin.json`, and `integrity.json` (the SHA-256 of every file), and **no signature**.

```powershell
dotnet modularruntime inspect bin\Release\modularruntime\com.example.myfirstplugin-1.0.0.zip
```

## 7. Install it (Store)

In the desktop app open **Store → Install from ZIP**, choose the file, read the daemon's
verification, tick *I understand the origin is not verified*, and select *Install*. The plugin
appears on the **Plugins** page as *Local, unverified*. It is never started automatically.

With the CLI (non-interactive, explicit acceptance):

```powershell
modularruntime plugins install-zip bin\Release\modularruntime\com.example.myfirstplugin-1.0.0.zip --allow-unverified
modularruntime plugins packages
```

## 8. Start it

Select *Start* on the Plugins page, or run `modularruntime plugins start com.example.myfirstplugin`.

## 9. Read the logs

Open the **Logs** page, or run `modularruntime logs --plugin com.example.myfirstplugin`. The basic
plugin writes `MyFirstPlugin 1.0.0 started (plugin 'com.example.myfirstplugin', instance …)`.

## 10. Stop and restart

Select *Stop*, *Start*, or *Restart*, or run:

```powershell
modularruntime plugins stop com.example.myfirstplugin
modularruntime plugins start com.example.myfirstplugin
modularruntime plugins restart com.example.myfirstplugin
```

Every start creates a new instance in a new load context, after the package is verified again.

## Next

- Change the code, run `dotnet modularruntime pack` again, and update with *Install from ZIP*.
  A running version must be stopped (the app asks; with the CLI pass `--stop-if-running`).
- Learn the API: [PLUGIN_API.md](PLUGIN_API.md).
- Publish officially: [PLUGIN_SUBMISSION_GUIDE.md](PLUGIN_SUBMISSION_GUIDE.md).
