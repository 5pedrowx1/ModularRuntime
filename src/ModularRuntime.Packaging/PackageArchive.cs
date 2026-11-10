using System.IO.Compression;
using System.Security.Cryptography;

namespace ModularRuntime.Packaging;

/// <summary>
/// A plugin package (ZIP) opened for inspection and safe extraction. Never loads or runs anything.
/// </summary>
/// <remarks>
/// <para>
/// Opening validates every entry before anything is extracted: entry count, path rules
/// (<see cref="PackagePaths"/>), entry types (no symbolic links or reparse points), duplicates and
/// case-insensitive collisions (including a file colliding with a directory), declared sizes, and
/// compression ratios. Extraction then enforces the size limits on the bytes actually produced by
/// decompression, writes only new files below the target directory, and hashes while writing.
/// </para>
/// <para>
/// The archive must be a daemon-owned staged copy: the file is opened without write or delete
/// sharing, so it cannot change while it is read.
/// </para>
/// </remarks>
public sealed class PackageArchive : IDisposable
{
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private const int WindowsReparsePoint = 0x400;

    private readonly FileStream _stream;
    private readonly ZipArchive _zip;
    private readonly PackageLimits _limits;
    private readonly List<ZipArchiveEntry> _content = [];

    private PackageArchive(FileStream stream, ZipArchive zip, PackageLimits limits)
    {
        _stream = stream;
        _zip = zip;
        _limits = limits;
    }

    /// <summary>Content entries (everything except directory entries and signature metadata).</summary>
    public IReadOnlyList<ZipArchiveEntry> ContentEntries => _content;

    /// <summary>Exact bytes of <c>integrity.json</c>, or <see langword="null"/> when absent.</summary>
    public byte[]? IntegrityManifestBytes { get; private set; }

    /// <summary>Exact bytes of <c>integrity.sig</c>, or <see langword="null"/> when absent.</summary>
    public byte[]? SignatureBytes { get; private set; }

    /// <summary>Total declared uncompressed size of the content files.</summary>
    public long DeclaredContentBytes { get; private set; }

    public static PackageArchive Open(string packagePath, PackageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var info = new FileInfo(packagePath);
        if (!info.Exists)
        {
            throw new PackageException(PackageErrorCodes.PackageCorrupt, "The package file does not exist.");
        }

        if (info.Length > limits.MaxPackageBytes)
        {
            throw new PackageException(PackageErrorCodes.PackageTooLarge, $"The package is larger than {limits.MaxPackageBytes} bytes.");
        }

        var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException)
        {
            stream.Dispose();
            throw new PackageException(PackageErrorCodes.PackageCorrupt, $"The package is not a valid ZIP archive: {exception.Message}");
        }

        var archive = new PackageArchive(stream, zip, limits);
        try
        {
            archive.ValidateEntries();
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Extracts every content entry below <paramref name="targetDirectory"/> (which must be empty or
    /// not exist) and returns each file's path, actual size, and SHA-256.
    /// </summary>
    public async Task<IReadOnlyList<IntegrityFile>> ExtractAsync(
        string targetDirectory,
        IProgress<long>? bytesWritten = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(targetDirectory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new PackageException(PackageErrorCodes.InstallationFailed, "The extraction directory is not empty.");
        }

        Directory.CreateDirectory(root);
        var results = new List<IntegrityFile>(_content.Count);
        long total = 0;
        var buffer = new byte[81920];
        foreach (var entry in _content)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = PackagePaths.Resolve(root, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            try
            {
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    total += read;
                    if (size > _limits.MaxFileBytes || size > entry.Length)
                    {
                        throw new PackageException(PackageErrorCodes.ExpansionLimitExceeded, $"'{entry.FullName}' expands beyond its declared or allowed size.", entry.FullName);
                    }

                    if (total > _limits.MaxTotalBytes)
                    {
                        throw new PackageException(PackageErrorCodes.ExpansionLimitExceeded, $"The package expands beyond {_limits.MaxTotalBytes} bytes.", entry.FullName);
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    bytesWritten?.Report(total);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                throw new PackageException(PackageErrorCodes.PackageCorrupt, $"'{entry.FullName}' could not be decompressed: {exception.Message}", entry.FullName);
            }

            if (size != entry.Length)
            {
                throw new PackageException(PackageErrorCodes.PackageCorrupt, $"'{entry.FullName}' is shorter than its declared size.", entry.FullName);
            }

            results.Add(new IntegrityFile(entry.FullName, size, Convert.ToHexStringLower(hash.GetHashAndReset())));
        }

        return results;
    }

    public void Dispose()
    {
        _zip.Dispose();
        _stream.Dispose();
    }

    private void ValidateEntries()
    {
        if (_zip.Entries.Count > _limits.MaxEntries)
        {
            throw new PackageException(PackageErrorCodes.TooManyEntries, $"The package has more than {_limits.MaxEntries} entries.");
        }

        var files = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        long declared = 0;

        foreach (var entry in _zip.Entries)
        {
            var name = entry.FullName;
            var isDirectory = name.EndsWith('/');
            var path = isDirectory ? name[..^1] : name;

            PackagePaths.Validate(path, _limits);
            ValidateEntryType(entry, isDirectory);

            var key = PackagePaths.ComparisonKey(path);
            if (isDirectory)
            {
                if (entry.Length != 0)
                {
                    throw new PackageException(PackageErrorCodes.PackageCorrupt, $"Directory entry '{PackagePaths.Describe(name)}' has content.", name);
                }

                if (files.Contains(key))
                {
                    throw Collision(name);
                }

                directories.Add(key);
                continue;
            }

            if (!files.Add(key) || directories.Contains(key))
            {
                throw Collision(name);
            }

            // Every parent of a file is an implicit directory; it must not also be a file.
            var parent = path;
            int slash;
            while ((slash = parent.LastIndexOf('/')) > 0)
            {
                parent = parent[..slash];
                var parentKey = PackagePaths.ComparisonKey(parent);
                if (files.Contains(parentKey))
                {
                    throw Collision(name);
                }

                directories.Add(parentKey);
            }

            if (entry.Length < 0 || entry.Length > _limits.MaxFileBytes)
            {
                throw new PackageException(PackageErrorCodes.EntryTooLarge, $"'{PackagePaths.Describe(name)}' is larger than {_limits.MaxFileBytes} bytes.", name);
            }

            if (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 &&
                entry.Length / entry.CompressedLength > _limits.MaxCompressionRatio)
            {
                throw new PackageException(PackageErrorCodes.ExpansionLimitExceeded, $"'{PackagePaths.Describe(name)}' has a suspicious compression ratio.", name);
            }

            if (PackagePaths.IsMetadataFile(path))
            {
                if (path.Contains('/', StringComparison.Ordinal) || !string.Equals(path, path.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    throw new PackageException(PackageErrorCodes.UnsafeEntryPath, $"'{PackagePaths.Describe(name)}' must be named exactly '{path.ToLowerInvariant()}' at the package root.", name);
                }

                var bytes = ReadSmall(entry, path == PackagePaths.IntegrityManifestName ? _limits.MaxManifestBytes : _limits.MaxSignatureBytes);
                if (path == PackagePaths.IntegrityManifestName)
                {
                    IntegrityManifestBytes = bytes;
                }
                else
                {
                    SignatureBytes = bytes;
                }

                continue;
            }

            declared += entry.Length;
            if (declared > _limits.MaxTotalBytes)
            {
                throw new PackageException(PackageErrorCodes.ExpansionLimitExceeded, $"The package declares more than {_limits.MaxTotalBytes} bytes of content.");
            }

            _content.Add(entry);
        }

        if (_content.Count == 0)
        {
            throw new PackageException(PackageErrorCodes.PackageCorrupt, "The package has no content files.");
        }

        _content.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        DeclaredContentBytes = declared;
    }

    private static void ValidateEntryType(ZipArchiveEntry entry, bool isDirectory)
    {
        var attributes = entry.ExternalAttributes;
        var unixType = (attributes >> 16) & UnixFileTypeMask;
        if (unixType != 0 && unixType != UnixRegularFile && unixType != UnixDirectory)
        {
            throw new PackageException(PackageErrorCodes.UnsupportedEntryType, $"'{PackagePaths.Describe(entry.FullName)}' is a symbolic link or special file.", entry.FullName);
        }

        if ((attributes & WindowsReparsePoint) != 0)
        {
            throw new PackageException(PackageErrorCodes.UnsupportedEntryType, $"'{PackagePaths.Describe(entry.FullName)}' is a reparse point.", entry.FullName);
        }

        if (!isDirectory && unixType == UnixDirectory)
        {
            throw new PackageException(PackageErrorCodes.UnsupportedEntryType, $"'{PackagePaths.Describe(entry.FullName)}' is marked as a directory but named as a file.", entry.FullName);
        }
    }

    private static byte[] ReadSmall(ZipArchiveEntry entry, int maxBytes)
    {
        if (entry.Length > maxBytes)
        {
            throw new PackageException(PackageErrorCodes.EntryTooLarge, $"'{entry.FullName}' is larger than {maxBytes} bytes.", entry.FullName);
        }

        try
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                if (output.Length + read > maxBytes)
                {
                    throw new PackageException(PackageErrorCodes.ExpansionLimitExceeded, $"'{entry.FullName}' expands beyond {maxBytes} bytes.", entry.FullName);
                }

                output.Write(buffer, 0, read);
            }

            if (output.Length != entry.Length)
            {
                throw new PackageException(PackageErrorCodes.PackageCorrupt, $"'{entry.FullName}' does not match its declared size.", entry.FullName);
            }

            return output.ToArray();
        }
        catch (InvalidDataException exception)
        {
            throw new PackageException(PackageErrorCodes.PackageCorrupt, $"'{entry.FullName}' could not be decompressed: {exception.Message}", entry.FullName);
        }
    }

    private static PackageException Collision(string name) =>
        new(PackageErrorCodes.DuplicateEntry, $"'{PackagePaths.Describe(name)}' collides with another entry (Windows paths are case-insensitive).", name);
}
