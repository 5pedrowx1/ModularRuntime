using System.IO.Compression;

namespace ModularRuntime.Packaging;

/// <summary>
/// Builds packages from a directory of approved build artifacts: the integrity manifest and the
/// deterministic ZIP. Signing is a separate step (<see cref="SignatureEnvelope.Sign"/>) so it can run
/// in a more restricted environment than preparation.
/// </summary>
public static class PackageBuilder
{
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Lists and hashes every file below <paramref name="contentDirectory"/> and validates
    /// <c>plugin.json</c> (without loading anything). Fails on unsafe names, reparse points, or
    /// signature metadata files in the content.
    /// </summary>
    public static async Task<IntegrityManifest> CreateManifestAsync(string contentDirectory, PackageLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= PackageLimits.Default;
        var root = Path.GetFullPath(contentDirectory);
        var candidate = await PackageVerifier.ReadPluginManifestAsync(root, cancellationToken).ConfigureAwait(false);
        var files = new List<IntegrityFile>();
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
        {
            var path = PackagePaths.ToPackagePath(root, entry.FullName);
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PackageException(PackageErrorCodes.UnsupportedEntryType, $"'{path}' is a reparse point or symbolic link.", path);
            }

            if (entry is DirectoryInfo)
            {
                continue;
            }

            PackagePaths.Validate(path, limits);
            if (PackagePaths.IsMetadataFile(path))
            {
                throw new PackageException(PackageErrorCodes.UnsafeEntryPath, $"'{path}' is reserved for signature metadata.", path);
            }

            var size = ((FileInfo)entry).Length;
            total += size;
            if (size > limits.MaxFileBytes || total > limits.MaxTotalBytes || files.Count >= limits.MaxEntries)
            {
                throw new PackageException(PackageErrorCodes.ExpansionLimitExceeded, "The artifacts exceed the package limits.", path);
            }

            files.Add(new IntegrityFile(path, size, await Sha256Hex.ComputeFileAsync(entry.FullName, cancellationToken).ConfigureAwait(false)));
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!keys.Add(PackagePaths.ComparisonKey(file.Path)))
            {
                throw new PackageException(PackageErrorCodes.DuplicateEntry, $"'{file.Path}' collides with another file.", file.Path);
            }
        }

        return IntegrityManifest.Create(candidate.Descriptor.Id, candidate.Descriptor.Version, files);
    }

    /// <summary>
    /// Writes the package ZIP: content files in ordinal path order with a fixed timestamp, then
    /// <c>integrity.json</c> (the exact bytes given) and <c>integrity.sig</c> when present. Each
    /// content file is re-hashed while it is written and must still match the manifest.
    /// </summary>
    public static void WriteZip(string contentDirectory, IntegrityManifest manifest, byte[] manifestBytes, byte[]? signatureBytes, string zipPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(manifestBytes);
        var root = Path.GetFullPath(contentDirectory);
        using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var file in manifest.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(PackagePaths.Resolve(root, file.Path));
            if (bytes.Length != file.Size || !Sha256Hex.Equal(Sha256Hex.Compute(bytes), file.Sha256))
            {
                throw new PackageException(PackageErrorCodes.IntegrityMismatch, $"'{file.Path}' changed after the manifest was created.", file.Path);
            }

            AddEntry(zip, file.Path, bytes);
        }

        AddEntry(zip, PackagePaths.IntegrityManifestName, manifestBytes);
        if (signatureBytes is not null)
        {
            AddEntry(zip, PackagePaths.IntegritySignatureName, signatureBytes);
        }
    }

    private static void AddEntry(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedTimestamp;
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}
