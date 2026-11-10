# ModularRuntime.Templates

`dotnet new` templates for ModularRuntime plugins.

```
dotnet new install ModularRuntime.Templates
dotnet new modularruntime-plugin -n MyFirstPlugin
dotnet new modularruntime-plugin -n Greeter --kind event-publisher --topic contoso.greetings
dotnet new modularruntime-plugin -n Upper --kind service-provider --service-id contoso.text --plugin-id com.contoso.upper
```

Kinds: `basic` (default), `event-publisher`, `event-subscriber`, `service-provider`,
`service-consumer`, `extension`. Options: `--plugin-id`, `--plugin-name`, `--plugin-author`, `--topic`,
`--service-id`, `--extension-point`, `--sdk-version`, `--no-restore`.

The generated project targets net10.0, references `ModularRuntime.Sdk`, and contains a valid
`plugin.json`. Validate and pack it with the developer tool (`ModularRuntime.DevTools`):
`dotnet modularruntime validate` and `dotnet modularruntime pack`.
