using System.Text;
using ModularRuntime.Packaging;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Packaging.Tests;

public sealed class PackagePathTests
{
    [Theory]
    [InlineData("plugin.json")]
    [InlineData("HelloPlugin.dll")]
    [InlineData("dependencies/Library.dll")]
    [InlineData("runtimes/win-x64/native/lib.dll")]
    [InlineData("readme.md")]
    public void SafePaths_AreAccepted(string path) =>
        Assert.True(PackagePaths.TryValidate(path, PackageLimits.Default, out _));

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("lib/../../evil.dll")]
    [InlineData("./plugin.json")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("c:evil.dll")]
    [InlineData("//server/share/evil.dll")]
    [InlineData("\\\\server\\share\\evil.dll")]
    [InlineData("lib\\evil.dll")]
    [InlineData("\\\\?\\C:\\evil.dll")]
    [InlineData("plugin.dll:hidden")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("lib/NUL.dll")]
    [InlineData("COM1.json")]
    [InlineData("LPT¹.dll")]
    [InlineData("trailingdot.")]
    [InlineData("trailingspace ")]
    [InlineData("a//b.dll")]
    [InlineData("lib/")]
    [InlineData("")]
    [InlineData("bad\u0001name.dll")]
    [InlineData("what?.dll")]
    [InlineData("pipe|name.dll")]
    public void DangerousPaths_AreRejected(string path) =>
        Assert.False(PackagePaths.TryValidate(path, PackageLimits.Default, out _));

    [Fact]
    public void NonNormalizedUnicode_IsRejected()
    {
        var decomposed = "cafe\u0301.dll"; // "café" in form D
        Assert.False(PackagePaths.TryValidate(decomposed, PackageLimits.Default, out var reason));
        Assert.Contains("normalization", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DepthAndLength_AreLimited()
    {
        var limits = new PackageLimits { MaxDepth = 3, MaxPathLength = 20 };
        Assert.True(PackagePaths.TryValidate("a/b/c.dll", limits, out _));
        Assert.False(PackagePaths.TryValidate("a/b/c/d.dll", limits, out _));
        Assert.False(PackagePaths.TryValidate(new string('x', 21), limits, out _));
    }

    [Fact]
    public void Resolve_StaysInsideTheRoot()
    {
        using var temp = new TemporaryDirectory("paths");
        Assert.StartsWith(temp.DirectoryPath, PackagePaths.Resolve(temp.DirectoryPath, "lib/a.dll"), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class MaliciousArchiveTests : IDisposable
{
    private const int UnixSymlink = unchecked((int)0xA1FF0000);
    private const int WindowsReparsePoint = 0x400;

    private readonly TemporaryDirectory _temp = new("archive");

    public void Dispose() => _temp.Dispose();

    private string Zip(params (string Name, string Data)[] entries) =>
        TestPackages.WriteRawZip(Path.Combine(_temp.DirectoryPath, Guid.NewGuid().ToString("N") + ".zip"),
            [.. entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Data), 0))]);

    private static PackageException Open(string zip, PackageLimits? limits = null) =>
        Assert.Throws<PackageException>(() => PackageArchive.Open(zip, limits ?? PackageLimits.Default).Dispose());

    [Fact]
    public void ValidArchive_Opens_AndListsContentInOrdinalOrder()
    {
        using var archive = PackageArchive.Open(Zip(("b.dll", "b"), ("plugin.json", "{}"), ("lib/", ""), ("lib/a.dll", "a")), PackageLimits.Default);
        Assert.Equal(["b.dll", "lib/a.dll", "plugin.json"], archive.ContentEntries.Select(e => e.FullName));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("lib/../../evil.dll")]
    [InlineData("/abs/evil.dll")]
    [InlineData("C:/evil.dll")]
    [InlineData("//server/share/evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("evil.dll:stream")]
    [InlineData("AUX.dll")]
    public void UnsafeEntryNames_AreRejected(string name) =>
        Assert.Equal(PackageErrorCodes.UnsafeEntryPath, Open(Zip(("plugin.json", "{}"), (name, "x"))).Code);

    [Fact]
    public void DuplicateNames_AreRejected() =>
        Assert.Equal(PackageErrorCodes.DuplicateEntry, Open(Zip(("a.dll", "1"), ("a.dll", "2"))).Code);

    [Fact]
    public void CaseInsensitiveCollisions_AreRejected() =>
        Assert.Equal(PackageErrorCodes.DuplicateEntry, Open(Zip(("Plugin.dll", "1"), ("plugin.DLL", "2"))).Code);

    [Fact]
    public void FileAndDirectoryWithTheSameName_AreRejected() =>
        Assert.Equal(PackageErrorCodes.DuplicateEntry, Open(Zip(("lib", "file"), ("LIB/a.dll", "2"))).Code);

    [Fact]
    public void SymbolicLinks_AreRejected()
    {
        var zip = TestPackages.WriteRawZip(Path.Combine(_temp.DirectoryPath, "link.zip"), ("link.dll", Encoding.UTF8.GetBytes("C:/Windows/System32/kernel32.dll"), UnixSymlink));
        Assert.Equal(PackageErrorCodes.UnsupportedEntryType, Open(zip).Code);
    }

    [Fact]
    public void ReparsePoints_AreRejected()
    {
        var zip = TestPackages.WriteRawZip(Path.Combine(_temp.DirectoryPath, "reparse.zip"), ("junction", [], WindowsReparsePoint));
        Assert.Equal(PackageErrorCodes.UnsupportedEntryType, Open(zip).Code);
    }

    [Fact]
    public void MetadataNamesWithOtherCase_AreRejected_AndNestedFilesWithThoseNamesAreContent()
    {
        Assert.Equal(PackageErrorCodes.UnsafeEntryPath, Open(Zip(("plugin.json", "{}"), ("INTEGRITY.JSON", "{}"))).Code);
        Assert.Equal(PackageErrorCodes.UnsafeEntryPath, Open(Zip(("plugin.json", "{}"), ("Integrity.Sig", "x"))).Code);

        // Only the package root holds signature metadata; a nested file is ordinary, hashed content.
        using var archive = PackageArchive.Open(Zip(("plugin.json", "{}"), ("docs/integrity.json", "{}")), PackageLimits.Default);
        Assert.Contains(archive.ContentEntries, e => e.FullName == "docs/integrity.json");
        Assert.Null(archive.IntegrityManifestBytes);
    }

    [Fact]
    public void TruncatedArchive_IsRejected()
    {
        var zip = Zip(("plugin.json", "{}"), ("a.dll", new string('x', 5000)));
        var bytes = File.ReadAllBytes(zip);
        File.WriteAllBytes(zip, bytes[..(bytes.Length / 2)]);
        Assert.Equal(PackageErrorCodes.PackageCorrupt, Open(zip).Code);
    }

    [Fact]
    public void NotAZip_IsRejected()
    {
        var path = Path.Combine(_temp.DirectoryPath, "text.zip");
        File.WriteAllText(path, "this is not a zip");
        Assert.Equal(PackageErrorCodes.PackageCorrupt, Open(path).Code);
    }

    [Fact]
    public void TooManyEntries_AreRejected()
    {
        var zip = Zip([.. Enumerable.Range(0, 11).Select(i => ($"f{i}.txt", "x"))]);
        Assert.Equal(PackageErrorCodes.TooManyEntries, Open(zip, new PackageLimits { MaxEntries = 10 }).Code);
    }

    [Fact]
    public void ExcessiveDepth_IsRejected() =>
        Assert.Equal(PackageErrorCodes.UnsafeEntryPath, Open(Zip(("a/b/c/d/e.dll", "x")), new PackageLimits { MaxDepth = 4 }).Code);

    [Fact]
    public void ZipBomb_DeclaredSizeOverTheLimit_IsRejectedBeforeExtraction()
    {
        var zip = TestPackages.WriteRawZip(Path.Combine(_temp.DirectoryPath, "bomb.zip"), ("zeros.bin", new byte[4 * 1024 * 1024], 0));
        Assert.Equal(PackageErrorCodes.EntryTooLarge, Open(zip, new PackageLimits { MaxFileBytes = 1024 * 1024 }).Code);
    }

    [Fact]
    public void ZipBomb_CompressionRatio_IsRejected()
    {
        var zip = TestPackages.WriteRawZip(Path.Combine(_temp.DirectoryPath, "ratio.zip"), ("zeros.bin", new byte[8 * 1024 * 1024], 0));
        Assert.Equal(PackageErrorCodes.ExpansionLimitExceeded, Open(zip, new PackageLimits { MaxCompressionRatio = 50 }).Code);
    }

    [Fact]
    public void ZipBomb_TotalSize_IsRejected()
    {
        var zip = Zip(("a.bin", new string('a', 600)), ("b.bin", new string('b', 600)));
        Assert.Equal(PackageErrorCodes.ExpansionLimitExceeded, Open(zip, new PackageLimits { MaxTotalBytes = 1000 }).Code);
    }

    [Fact]
    public async Task Extraction_EnforcesRealLimits_AndOnlyWritesInsideTheTarget()
    {
        var zip = Zip(("plugin.json", "{}"), ("lib/a.dll", "hello"));
        using var archive = PackageArchive.Open(zip, PackageLimits.Default);
        var target = Path.Combine(_temp.DirectoryPath, "out");

        var files = await archive.ExtractAsync(target);

        Assert.Equal(2, files.Count);
        Assert.Equal(Sha256Hex.Compute(Encoding.UTF8.GetBytes("hello")), files.Single(f => f.Path == "lib/a.dll").Sha256);
        Assert.Equal(["lib", "plugin.json"], Directory.GetFileSystemEntries(target).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Extraction_RefusesANonEmptyTarget()
    {
        using var archive = PackageArchive.Open(Zip(("plugin.json", "{}")), PackageLimits.Default);
        var target = Path.Combine(_temp.DirectoryPath, "busy");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "existing.txt"), "keep me");

        var error = await Assert.ThrowsAsync<PackageException>(() => archive.ExtractAsync(target));

        Assert.Equal(PackageErrorCodes.InstallationFailed, error.Code);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(target, "existing.txt")));
    }

    [Fact]
    public async Task Extraction_CanBeCanceled()
    {
        using var archive = PackageArchive.Open(Zip([.. Enumerable.Range(0, 50).Select(i => ($"f{i}.txt", new string('x', 1000)))]), PackageLimits.Default);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.ExtractAsync(Path.Combine(_temp.DirectoryPath, "c"), cancellationToken: cancel.Token));
    }
}
