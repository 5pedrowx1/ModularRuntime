# ModularRuntime.DevTools

The `dotnet modularruntime` tool for ModularRuntime plugin authors.

```
dotnet tool install --global ModularRuntime.DevTools
dotnet modularruntime validate            # static checks of the project and plugin.json
dotnet modularruntime pack                # build (Release) and create a local package
dotnet modularruntime inspect <file.zip>  # verify a package without running it
dotnet modularruntime prepare-submission  # source + package + hashes for official review
```

`pack` produces a **local, unsigned** package. The Plugin Manager installs it as
"Local, unverified" only after explicit acceptance. The hashes in `integrity.json` are declared
by the tool: they show the files are consistent, not who made them or that anyone reviewed them.
Official signatures are produced only by the ModularRuntime team's publishing process, after
review and a controlled rebuild. This tool never has access to signing keys.

The tool runs without the daemon. It never loads or runs plugin code: `validate` and `inspect`
read assemblies as metadata only, and builds always go through `dotnet build`.
