using System.Globalization;
using ModularRuntime.Packaging;
using ModularRuntime.Registry;

namespace ModularRuntime.DevTools;

/// <summary>
/// <c>dotnet modularruntime registry-check &lt;registry.json&gt;</c>: the public verification of a signed
/// catalog revision (<see cref="RegistryCatalogCheck"/>), as run by the public registry's CI. Needs
/// no private key; reads only the files named on the command line.
/// </summary>
internal static class RegistryCheckCommand
{
    public static int Run(Arguments arguments, TextWriter output)
    {
        if (arguments.Positionals.Count != 1)
        {
            throw new DevToolsException("registry-check needs exactly one catalog: dotnet modularruntime registry-check <registry.json>");
        }

        var registryPath = Path.GetFullPath(arguments.Positionals[0]);
        var signaturePath = Path.GetFullPath(arguments.Value("signature") ?? Path.Combine(Path.GetDirectoryName(registryPath)!, "registry.sig"));
        var trust = arguments.Value("trust") is { } trustPath
            ? TrustStore.Parse(File.ReadAllBytes(trustPath), allowTestKeys: false)
            : TrustStore.LoadOfficial();
        if (arguments.Value("test-trust-store") is { } testTrustPath)
        {
            trust = trust.Merge(TrustStore.Parse(File.ReadAllBytes(testTrustPath), allowTestKeys: true));
        }

        var maxDays = 90;
        if (arguments.Value("max-validity-days") is { } days &&
            (!int.TryParse(days, NumberStyles.None, CultureInfo.InvariantCulture, out maxDays) || maxDays is < 1 or > 400))
        {
            throw new DevToolsException("--max-validity-days must be a whole number of days between 1 and 400.");
        }

        RegistryCheckResult result;
        try
        {
            result = RegistryCatalogCheck.Check(
                File.ReadAllBytes(registryPath),
                File.ReadAllBytes(signaturePath),
                trust,
                new RegistryCheckOptions
                {
                    PreviousRegistry = arguments.Value("previous") is { } previous ? File.ReadAllBytes(previous) : null,
                    PackagesRoot = arguments.Value("packages-root"),
                    MaxValidity = TimeSpan.FromDays(maxDays),
                });
        }
        catch (RegistryException exception)
        {
            throw new DevToolsException($"[{exception.Code}] {exception.Message}");
        }

        var document = result.Document;
        output.WriteLine(
            $"OK: registry revision {document.Revision} (schema {document.SchemaVersion}), {document.Plugins.Count} entries " +
            $"({document.Plugins.Count(p => p.IsPublished)} published, {document.Plugins.Count(p => p.EffectiveStatus == RegistryEntryStatus.Withdrawn)} withdrawn, {document.Plugins.Count(p => p.IsRevoked)} revoked), " +
            $"{document.RevokedKeys?.Count ?? 0} revoked key(s), signed by '{result.SigningKey.KeyId}'{(result.SigningKey.IsTestKey ? " (TEST KEY)" : string.Empty)}, " +
            $"{result.CheckedPackages} package file(s) checked, expires {document.ExpiresAt:O}.");
        return ExitCodes.Success;
    }
}
