using ModularRuntime.Contracts;
using ModularRuntime.Packaging;

namespace ModularRuntime.DevTools;

/// <summary>
/// Inspects a package without running it: the same verification as the daemon (archive
/// structure and limits, signature with the trusted keys, integrity manifest, per-file SHA-256,
/// plugin.json, API compatibility) into a temporary directory, then metadata checks of the entry
/// assembly. Nothing is installed, loaded, or executed.
/// </summary>
internal static class PackageInspector
{
    public static async Task<bool> InspectAsync(string packagePath, string? testTrustStorePath, TextWriter output, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(packagePath);
        if (!File.Exists(path))
        {
            throw new DevToolsException($"'{path}' does not exist.");
        }

        var trustStore = TrustStore.LoadOfficial();
        if (testTrustStorePath is not null)
        {
            trustStore = trustStore.Merge(TrustStore.Parse(await File.ReadAllBytesAsync(testTrustStorePath, cancellationToken).ConfigureAwait(false), allowTestKeys: true));
        }

        output.WriteLine($"Package:     {path}");
        output.WriteLine($"Size:        {new FileInfo(path).Length} bytes");
        output.WriteLine($"SHA-256:     {await Sha256Hex.ComputeFileAsync(path, cancellationToken).ConfigureAwait(false)}");

        var extract = Path.Combine(Path.GetTempPath(), "modularruntime-inspect-" + Guid.NewGuid().ToString("N"));
        try
        {
            VerifiedPackage package;
            try
            {
                package = await new PackageVerifier(trustStore).VerifyAndExtractAsync(path, extract, new PackageExpectation(), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (PackageException exception)
            {
                output.WriteLine($"Result:      REJECTED [{exception.Code}] {exception.Message}");
                output.WriteLine("             The Plugin Manager would refuse this package.");
                return false;
            }

            var descriptor = package.Candidate.Descriptor;
            output.WriteLine($"Plugin:      {descriptor.Id}");
            output.WriteLine($"Name:        {descriptor.Name}");
            output.WriteLine($"Version:     {descriptor.Version}");
            if (package.Candidate.Author is { } author)
            {
                output.WriteLine($"Author:      {author}");
            }

            output.WriteLine($"Plugin API:  {descriptor.ApiVersion.ToString(2)} (this SDK: {PluginApiVersion.Supported.ToString(2)}; compatible)");
            output.WriteLine($"Entry:       {descriptor.EntryAssembly} → {descriptor.EntryType}");
            output.WriteLine(package.Trust == PackageTrust.Official
                ? $"Signature:   OFFICIAL — integrity.json signed by '{package.SigningKey!.KeyId}'{(package.SigningKey.IsTestKey ? " (TEST KEY: trusted only because a test trust store was given)" : string.Empty)}"
                : "Signature:   NONE — local, unverified package. The hashes in integrity.json are declared by whoever packed it: they prove the files are consistent, not who made them or that they were reviewed.");
            output.WriteLine($"Integrity:   every file matches integrity.json ({package.Files.Count} files, {package.Files.Sum(f => f.Size)} bytes)");

            var report = new ValidationReport();
            AssemblyMetadataInspector.Inspect(package.Candidate.EntryAssemblyPath, descriptor.EntryType, report);
            output.WriteLine(report.HasErrors ? "Assembly:    problems found (metadata only, nothing executed):" : "Assembly:    entry type found and implements IPlugin (metadata only, nothing executed)");
            report.WriteTo(output);

            output.WriteLine("Files:");
            foreach (var file in package.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                output.WriteLine($"  {file.Size,10}  {file.Sha256}  {file.Path}");
            }

            output.WriteLine(report.HasErrors ? "Result:      STRUCTURE OK, ASSEMBLY PROBLEMS (see above)" : $"Result:      VALID ({(package.Trust == PackageTrust.Official ? "official" : "local, unverified")})");
            return !report.HasErrors;
        }
        finally
        {
            PluginPacker.TryDelete(extract);
        }
    }
}
