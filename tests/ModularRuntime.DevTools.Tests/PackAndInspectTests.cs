using System.IO.Compression;
using System.Text;
using ModularRuntime.Packaging;
using ModularRuntime.TestSupport;

namespace ModularRuntime.DevTools.Tests;

/// <summary><c>pack</c> and <c>inspect</c>: the produced package is what the Plugin Manager accepts.</summary>
public sealed class PackAndInspectTests : IDisposable
{
    private readonly PluginProjectFixture _fixture = new("devtools-pack");

    public void Dispose() => _fixture.Dispose();

    private async Task<string> PackAsync(string project, params string[] extra)
    {
        var output = Path.Combine(_fixture.Root, "out-" + Guid.NewGuid().ToString("N")[..8]);
        var run = await PluginProjectFixture.RunAsync(["pack", "--project", project, "-o", output, .. extra]);
        Assert.True(run.ExitCode == 0, run.All);
        return Assert.Single(Directory.GetFiles(output, "*.zip"));
    }

    [Fact]
    public async Task Pack_ProducesAnUnsignedPackage_WithCorrectHashes_AndWithoutSharedOrDevelopmentFiles()
    {
        var project = _fixture.CreatePlugin();

        var zip = await PackAsync(project);

        Assert.Equal("test.sampleplugin-1.0.0.zip", Path.GetFileName(zip));
        var entries = PluginProjectFixture.ReadZip(zip);
        Assert.DoesNotContain(PackagePaths.IntegritySignatureName, entries.Keys); // never an official (or fake) signature
        Assert.DoesNotContain("ModularRuntime.Contracts.dll", entries.Keys); // shared with the host
        Assert.DoesNotContain(entries.Keys, k => k.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("SamplePlugin.dll", entries.Keys);
        Assert.Contains("plugin.json", entries.Keys);

        var manifest = IntegrityManifest.Parse(entries[PackagePaths.IntegrityManifestName], PackageLimits.Default);
        Assert.Equal("test.sampleplugin", manifest.PluginId.Value);
        Assert.Equal(entries.Keys.Where(k => k != PackagePaths.IntegrityManifestName).Order(StringComparer.Ordinal), manifest.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        foreach (var file in manifest.Files)
        {
            Assert.Equal(Sha256Hex.Compute(entries[file.Path]), file.Sha256);
            Assert.Equal(entries[file.Path].Length, file.Size);
        }

        // The daemon's verifier accepts it, as an unverified local package.
        using var extract = new TemporaryDirectory("devtools-verify");
        var verified = await new PackageVerifier(TrustStore.LoadOfficial()).VerifyAndExtractAsync(zip, Path.Combine(extract.DirectoryPath, "x"), new PackageExpectation());
        Assert.Equal(PackageTrust.Unverified, verified.Trust);
        Assert.Null(verified.SigningKey);
    }

    [Fact]
    public async Task Pack_IsDeterministic_ForTheSameBuildOutput_AndCanBeRepeated()
    {
        var project = _fixture.CreatePlugin();
        var first = await PackAsync(project);

        var second = await PackAsync(project, "--no-build");
        var third = await PackAsync(project, "--no-build");

        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        Assert.Equal(await File.ReadAllBytesAsync(second), await File.ReadAllBytesAsync(third));

        // Packing again to the same file replaces it.
        var again = await PluginProjectFixture.RunAsync("pack", "--project", project, "--no-build", "-o", first);
        Assert.Equal(0, again.ExitCode);
        Assert.Equal(await File.ReadAllBytesAsync(second), await File.ReadAllBytesAsync(first));
    }

    [Fact]
    public async Task Pack_IncludeSymbols_AddsPdbFiles()
    {
        var project = _fixture.CreatePlugin();

        var zip = await PackAsync(project, "--include-symbols");

        Assert.Contains("SamplePlugin.pdb", PluginProjectFixture.ReadZip(zip).Keys);
    }

    [Fact]
    public async Task Pack_InvalidProject_BuildsAndWritesNothing()
    {
        var project = _fixture.CreatePlugin(editManifest: m => m["id"] = "Not Valid");
        var output = Path.Combine(_fixture.Root, "invalid-out");

        var run = await PluginProjectFixture.RunAsync("pack", "--project", project, "-o", output);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("nothing was built or packed", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(project, "bin")));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Pack_BuildFailure_IsReported()
    {
        var project = _fixture.CreatePlugin(source: "this is not C#");

        var run = await PluginProjectFixture.RunAsync("pack", "--project", project, "-o", Path.Combine(_fixture.Root, "x"));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("The build failed", run.Output, StringComparison.Ordinal);
        Assert.Contains("error CS", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pack_NoBuildWithoutOutput_IsAnError()
    {
        var project = _fixture.CreatePlugin();

        var run = await PluginProjectFixture.RunAsync("pack", "--project", project, "--no-build");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Build the project first", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pack_Canceled_ExitsWith130_AndLeavesNoPackage()
    {
        var project = _fixture.CreatePlugin();
        var output = Path.Combine(_fixture.Root, "canceled");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        var run = await PluginProjectFixture.RunAsync(cancellation.Token, "pack", "--project", project, "-o", output);

        Assert.Equal(130, run.ExitCode);
        Assert.False(Directory.Exists(output) && Directory.EnumerateFiles(output).Any());
    }

    [Fact]
    public async Task PathsWithSpacesAndUnicode_AreSupported()
    {
        var directory = Path.Combine(_fixture.Root, "Pasta com espaços ção ünï");
        var project = _fixture.CreatePlugin("Plugin", directory);

        var zip = await PackAsync(project);
        var inspect = await PluginProjectFixture.RunAsync("inspect", zip);

        Assert.Equal(0, inspect.ExitCode);
    }

    [Fact]
    public async Task Inspect_ListsFilesHashesAndTheMissingSignature()
    {
        var zip = await PackAsync(_fixture.CreatePlugin());

        var run = await PluginProjectFixture.RunAsync("inspect", zip);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Plugin:      test.sampleplugin", run.Output, StringComparison.Ordinal);
        Assert.Contains("Signature:   NONE", run.Output, StringComparison.Ordinal);
        Assert.Contains("not who made them", run.Output, StringComparison.Ordinal);
        Assert.Contains("Result:      VALID (local, unverified)", run.Output, StringComparison.Ordinal);
        var entries = PluginProjectFixture.ReadZip(zip);
        Assert.Contains(Sha256Hex.Compute(entries["SamplePlugin.dll"]) + "  SamplePlugin.dll", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_MalformedOrMissingPackage_IsRejected()
    {
        var notZip = Path.Combine(_fixture.Root, "not-a-package.zip");
        await File.WriteAllTextAsync(notZip, "not a zip");

        var malformed = await PluginProjectFixture.RunAsync("inspect", notZip);
        var missing = await PluginProjectFixture.RunAsync("inspect", Path.Combine(_fixture.Root, "missing.zip"));
        var usage = await PluginProjectFixture.RunAsync("inspect");

        Assert.Equal(1, malformed.ExitCode);
        Assert.Contains("REJECTED [package_corrupt]", malformed.Output, StringComparison.Ordinal);
        Assert.Equal(1, missing.ExitCode);
        Assert.Equal(1, usage.ExitCode);
        Assert.Contains("needs exactly one package path", usage.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_UnsafePathsAndTamperedFiles_AreRejected()
    {
        var zip = await PackAsync(_fixture.CreatePlugin());
        var entries = PluginProjectFixture.ReadZip(zip);

        var traversal = Path.Combine(_fixture.Root, "traversal.zip");
        TestPackages.WriteRawZip(traversal, [.. entries.Select(e => (e.Key, e.Value, 0)), ("../evil.dll", Encoding.UTF8.GetBytes("x"), 0)]);
        var tampered = Path.Combine(_fixture.Root, "tampered.zip");
        TestPackages.WriteRawZip(tampered, [.. entries.Select(e => (e.Key, e.Key == "SamplePlugin.dll" ? [.. e.Value, 0] : e.Value, 0))]);

        var unsafeRun = await PluginProjectFixture.RunAsync("inspect", traversal);
        var tamperedRun = await PluginProjectFixture.RunAsync("inspect", tampered);

        Assert.Equal(1, unsafeRun.ExitCode);
        Assert.Contains("REJECTED [unsafe_entry_path]", unsafeRun.Output, StringComparison.Ordinal);
        Assert.Equal(1, tamperedRun.ExitCode);
        Assert.Contains("REJECTED [", tamperedRun.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_OfficialSignature_IsVerifiedWithTheGivenTrustStore_OnlyThen()
    {
        // A package signed by the publishing process (here with an ephemeral test key).
        var project = _fixture.CreatePlugin();
        var unsigned = await PackAsync(project);
        var content = Path.Combine(_fixture.Root, "content");
        ZipFile.ExtractToDirectory(unsigned, content);
        File.Delete(Path.Combine(content, PackagePaths.IntegrityManifestName));
        var manifest = await PackageBuilder.CreateManifestAsync(content);
        var manifestBytes = manifest.Serialize();
        using var key = new TestSigningKey("devtools-test-key");
        var signed = Path.Combine(_fixture.Root, "signed.zip");
        PackageBuilder.WriteZip(content, manifest, manifestBytes, key.Sign(manifestBytes), signed);
        var trustStore = TestPackages.WriteTrustStore(Path.Combine(_fixture.Root, "trust.json"), key);

        var withoutTrust = await PluginProjectFixture.RunAsync("inspect", signed);
        var withTrust = await PluginProjectFixture.RunAsync("inspect", signed, "--test-trust-store", trustStore);

        Assert.Equal(1, withoutTrust.ExitCode); // a signature by an unknown key is never downgraded to "unsigned"
        Assert.Contains("REJECTED", withoutTrust.Output, StringComparison.Ordinal);
        Assert.Equal(0, withTrust.ExitCode);
        Assert.Contains("Signature:   OFFICIAL", withTrust.Output, StringComparison.Ordinal);
        Assert.Contains("TEST KEY", withTrust.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareSubmission_CollectsSource_ExcludesSecretsAndBuildOutput_AndHashesEverything()
    {
        var project = _fixture.CreatePlugin();
        await File.WriteAllTextAsync(Path.Combine(project, "signing.pfx"), "binary");
        await File.WriteAllTextAsync(Path.Combine(project, ".env"), "TOKEN=secret");
        await File.WriteAllTextAsync(Path.Combine(project, "notes.txt"), "-----BEGIN EC PRIVATE KEY-----\nabc\n-----END EC PRIVATE KEY-----");
        Directory.CreateDirectory(Path.Combine(project, "config")); // not read by restore, still collected
        await File.WriteAllTextAsync(Path.Combine(project, "config", "nuget.config"), "<configuration><packageSourceCredentials><x><add key=\"ClearTextPassword\" value=\"p\" /></x></packageSourceCredentials></configuration>");
        await File.WriteAllTextAsync(Path.Combine(project, "SamplePlugin.csproj.user"), "<Project />");
        Directory.CreateDirectory(Path.Combine(project, "docs"));
        await File.WriteAllTextAsync(Path.Combine(project, "docs", "usage.md"), "# Usage");
        Assert.Equal(0, (await PluginProjectFixture.RunAsync("validate", "--project", project, "--build")).ExitCode);
        var destination = Path.Combine(_fixture.Root, "submission");

        var run = await PluginProjectFixture.RunAsync("prepare-submission", "--project", project, "-o", destination,
            "--license", "MIT", "--source-repository", "https://example.com/samples/plugin", "--source-commit", "0123456789abcdef0123456789abcdef01234567");

        Assert.True(run.ExitCode == 0, run.All);
        var source = Path.Combine(destination, "source");
        Assert.True(File.Exists(Path.Combine(source, "SamplePlugin.cs")));
        Assert.True(File.Exists(Path.Combine(source, "docs", "usage.md")));
        foreach (var excluded in new[] { "signing.pfx", ".env", "notes.txt", Path.Combine("config", "nuget.config"), "SamplePlugin.csproj.user", "bin", "obj" })
        {
            Assert.False(Path.Exists(Path.Combine(source, excluded)), $"{excluded} must not be submitted.");
        }

        var package = Assert.Single(Directory.GetFiles(Path.Combine(destination, "artifacts"), "*.zip"));
        var submission = TestPackages.ReadJson(Path.Combine(destination, "submission.json"));
        Assert.Equal("test.sampleplugin", submission["pluginId"]!.GetValue<string>());
        Assert.Equal("SamplePlugin.csproj", submission["project"]!.GetValue<string>());
        Assert.Equal(2, submission["schemaVersion"]!.GetValue<int>());
        Assert.Equal("MIT", submission["license"]!.GetValue<string>());
        Assert.Equal("https://example.com/samples/plugin", submission["sourceRepository"]!.GetValue<string>());
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", submission["sourceCommit"]!.GetValue<string>());
        Assert.Contains("not prove", submission["notice"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("contains a private key", submission["excluded"]!.ToJsonString(), StringComparison.Ordinal);

        var integrity = TestPackages.ReadJson(Path.Combine(destination, "source-integrity.json"));
        var files = integrity["files"]!.AsArray().Select(f => f!.AsObject()).ToList();
        Assert.Contains(files, f => f["path"]!.GetValue<string>() == "artifacts/" + Path.GetFileName(package));
        foreach (var file in files)
        {
            var path = Path.Combine(destination, file["path"]!.GetValue<string>());
            Assert.Equal(Sha256Hex.Compute(await File.ReadAllBytesAsync(path)), file["sha256"]!.GetValue<string>());
        }

        // Refuses to overwrite an existing submission.
        Assert.Equal(1, (await PluginProjectFixture.RunAsync("prepare-submission", "--project", project, "-o", destination, "--no-package")).ExitCode);

        // Without license, repository, and commit the submission is still written, with a clear warning.
        var incomplete = await PluginProjectFixture.RunAsync("prepare-submission", "--project", project, "-o", Path.Combine(_fixture.Root, "submission-incomplete"), "--no-package");
        Assert.Equal(0, incomplete.ExitCode);
        Assert.Contains("the official review requires", incomplete.All, StringComparison.Ordinal);
        Assert.Contains("--source-commit", incomplete.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_ReportsTheSdkAndTheProject()
    {
        var project = _fixture.CreatePlugin();

        var run = await PluginProjectFixture.RunAsync("doctor", "--project", project);

        Assert.True(run.ExitCode == 0, run.All);
        Assert.Contains(".NET SDK: 10.", run.Output, StringComparison.Ordinal);
        Assert.Contains("No problems found.", run.Output, StringComparison.Ordinal);
    }
}
