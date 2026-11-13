# ModularRuntime.Templates

`dotnet new` templates for ModularRuntime plugins.

```
dotnet new install ModularRuntime.Templates
dotnet new modularruntime-plugin -n MyFirstPlugin
dotnet new modularruntime-plugin -n Greeter --kind event-publisher --topic contoso.greetings
dotnet new modularruntime-plugin -n Upper --kind service-provider --service-id contoso.text --plugin-id com.contoso.upper
dotnet new modularruntime-plugin -n Telemetry --kind typed-provider --service-id contoso.telemetry --contract-assembly Contoso.Telemetry.Contracts
```

Kinds: `basic` (default), `event-publisher`, `event-subscriber`, `service-provider`,
`service-consumer`, `extension`, `typed-provider`, `typed-consumer` (with a `Contracts/` shared
contract project declared in `plugin.json`), `binary-provider`, `stream-publisher`,
`stream-subscriber`. Options: `--plugin-id`, `--plugin-name`, `--plugin-author`, `--topic`,
`--service-id`, `--stream-id`, `--extension-point`, `--contract-assembly`, `--sdk-version`, `--no-restore`.

The generated project targets net10.0, references `ModularRuntime.Sdk`, and contains a valid
`plugin.json`. Validate and pack it with the developer tool (`ModularRuntime.DevTools`):
`dotnet modularruntime validate` and `dotnet modularruntime pack`.
