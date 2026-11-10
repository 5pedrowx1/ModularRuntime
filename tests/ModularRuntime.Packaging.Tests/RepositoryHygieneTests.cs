using System.Text.RegularExpressions;
using ModularRuntime.Packaging;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Packaging.Tests;

/// <summary>
/// The public ModularRuntime repository on its own (no other repository is needed): no private key
/// material, no Hub or Registry content, the official trust store free of test keys, and Git
/// ignore rules that keep keys, credentials, publishing records, and build output out. This
/// repository depends on no other repository.
/// </summary>
public sealed partial class RepositoryHygieneTests
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", ".vs", "artifacts", "plugins", "TestResults", "node_modules"];

    private static IEnumerable<string> RepositoryFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (!SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
            {
                foreach (var file in RepositoryFiles(child))
                {
                    yield return file;
                }
            }
        }
    }

    [Fact]
    public void Repository_ContainsNoPrivateKeyMaterial_NorKeyFiles()
    {
        var files = RepositoryFiles(RepositoryLayout.Root).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            Assert.False(Path.GetExtension(file).ToLowerInvariant() is ".pem" or ".pfx" or ".p12" or ".key" or ".snk" or ".kdbx" or ".ppk",
                $"{file} looks like a key file.");
            if (new FileInfo(file).Length < 4 * 1024 * 1024)
            {
                // Test fixtures may contain a key HEADER to exercise the detectors, never key material.
                Assert.DoesNotMatch(PrivateKeyPattern(), File.ReadAllText(file));
            }
        }
    }

    [Fact]
    public void OfficialTrustStore_ContainsNoTestKeys()
    {
        // Empty until the key ceremony; a test key here would make test-signed packages "official".
        var official = TrustStore.LoadOfficial();
        Assert.DoesNotContain(official.Keys, k => k.IsTestKey);
        Assert.False(official.ContainsTestKeys);
    }

    [Fact]
    public void Repository_HoldsNoHubOrRegistryContent()
    {
        var root = RepositoryLayout.Root;
        Assert.False(Directory.Exists(Path.Combine(root, "repository-templates")), "Hub and Registry files live in their own repositories.");
        Assert.False(Directory.Exists(Path.Combine(root, "submissions")));
        foreach (var file in RepositoryFiles(root))
        {
            var name = Path.GetFileName(file);
            Assert.False(name is "approval.json" or "approval.sig" or "build-record.json" or "registry.sig" || name.EndsWith(".review.json", StringComparison.Ordinal),
                $"{file}: publishing records belong to the Hub or the offline signing machine.");
        }
    }

    [Fact]
    public void PublicRepository_HasNoAdministrativePublishingCode_AndNoDependencyOnIt()
    {
        // The official review, approval, signing, and publication tools live in a separate private
        // repository that depends on this one; this repository must build and test without them.
        var root = RepositoryLayout.Root;
        string[] administrative = ["ModularRuntime.Publishing", "ModularRuntime.Pipeline", "ModularRuntime.Publisher"];
        foreach (var project in administrative)
        {
            Assert.False(Directory.Exists(Path.Combine(root, "src", project)), $"src/{project} belongs to the private publishing repository.");
        }

        var solution = File.ReadAllText(Path.Combine(root, "ModularRuntime.sln"));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in RepositoryFiles(root).Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(file);
            foreach (var project in administrative)
            {
                Assert.DoesNotContain(project + ".csproj", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(project + ".exe", text, StringComparison.OrdinalIgnoreCase);
            }

            // Every project reference stays inside this repository: it depends on nothing else.
            foreach (Match reference in ProjectReferencePattern().Matches(text))
            {
                var target = reference.Groups[1].Value;
                if (target.Contains("$(", StringComparison.Ordinal) || target.Contains("@(", StringComparison.Ordinal) || target.Contains('%', StringComparison.Ordinal))
                {
                    continue; // MSBuild item transforms of in-repository items (samples, fixtures)
                }

                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, target.Replace('\\', Path.DirectorySeparatorChar)));
                Assert.True(resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), $"{file} references '{target}', outside this repository.");
            }
        }

        Assert.DoesNotContain("ModularRuntime.Publish", solution, StringComparison.Ordinal);

        // No administrative implementation anywhere in the tree (code, docs, scripts). The markers are
        // assembled here so this file does not match itself.
        string[] markers =
        [
            "class " + "ReleaseBinding", "class " + "PrivateKeyFile", "class " + "ControlledBuild", "class " + "SubmissionCase",
            "class " + "SubmissionValidator", "record " + "BuildPlan", "MODULARRUNTIME_" + "SIGNING_PASSPHRASE",
        ];
        foreach (var file in RepositoryFiles(root).Where(f => new FileInfo(f).Length < 4 * 1024 * 1024 && Path.GetExtension(f) is ".cs" or ".md" or ".ps1" or ".sh" or ".json" or ".yml" or ".csproj" or ".props"))
        {
            var text = File.ReadAllText(file);
            foreach (var marker in markers)
            {
                Assert.False(text.Contains(marker, StringComparison.Ordinal), $"{file} contains '{marker}', an administrative implementation detail.");
            }
        }
        foreach (var file in RepositoryFiles(Path.Combine(root, "src")).Concat(RepositoryFiles(Path.Combine(root, "tests"))).Where(f => f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            Assert.DoesNotMatch(AdministrativeNamespacePattern(), File.ReadAllText(file));
        }
    }

    [Fact]
    public void GitIgnore_KeepsKeysCredentialsRecordsAndBuildOutputOut()
    {
        var ignore = File.ReadAllText(Path.Combine(RepositoryLayout.Root, ".gitignore"));
        foreach (var pattern in new[]
        {
            "*.pem", "*.pfx", "*.p12", "*.key", ".env", "secrets.json",
            "approval.json", "approval.sig", "/submissions/", "prepared/",
            "bin/", "obj/", "artifacts/", "plugins/*",
        })
        {
            Assert.Contains(pattern, ignore, StringComparison.Ordinal);
        }

        Assert.Contains("!src/**/OfficialTrustStore.json", ignore, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----\r?\n[A-Za-z0-9+/=]{16,}")]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(@"\b(namespace|using)\s+ModularRuntime\.(Publishing|Pipeline|Publisher)\b")]
    private static partial Regex AdministrativeNamespacePattern();

    [GeneratedRegex(@"<ProjectReference\s+Include=""([^""]+)""")]
    private static partial Regex ProjectReferencePattern();
}
