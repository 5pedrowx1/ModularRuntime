using System.Text.Json;
using System.Text.Json.Serialization;
using ModularRuntime.Contracts;

namespace ModularRuntime.Packaging;

/// <summary>Where an installed package came from.</summary>
public enum PackageSource
{
    /// <summary>Downloaded from the official registry.</summary>
    Registry,

    /// <summary>A ZIP file supplied by the user.</summary>
    LocalZip,
}

/// <summary>Persistent description of a managed installation (<c>packages/installed/&lt;id&gt;/record.json</c>).</summary>
public sealed record InstalledPackageRecord
{
    public int SchemaVersion { get; init; } = 1;

    public required string PluginId { get; init; }

    public required string Version { get; init; }

    public required string Name { get; init; }

    public string? Author { get; init; }

    public string? Description { get; init; }

    /// <summary>Directory name below the plugin root.</summary>
    public required string Directory { get; init; }

    public required PackageSource Source { get; init; }

    public required PackageTrust Trust { get; init; }

    public string? SigningKeyId { get; init; }

    public bool SignedWithTestKey { get; init; }

    public required string PackageSha256 { get; init; }

    public long PackageSize { get; init; }

    public int FileCount { get; init; }

    public long? RegistryRevision { get; init; }

    public required DateTimeOffset InstalledAt { get; init; }
}

/// <summary>Result of checking an installation against its recorded, possibly signed, file list.</summary>
public enum InstallationStatus
{
    /// <summary>Signature (official) and every file match.</summary>
    Verified,

    /// <summary>Files were added, removed, or changed since installation.</summary>
    Modified,

    /// <summary>The official signature no longer verifies (bad signature, unknown or revoked key).</summary>
    SignatureInvalid,

    /// <summary>The installation directory or its record is missing or unreadable.</summary>
    Missing,
}

public sealed record InstallationVerification(
    string PluginId,
    InstallationStatus Status,
    string Message,
    IReadOnlyList<string> Problems,
    DateTimeOffset CheckedAt);

/// <summary>
/// Managed plugin installations: atomic publish and removal, records, crash recovery, and
/// verification. Owned by the daemon; nothing else writes these directories.
/// </summary>
/// <remarks>
/// <para>
/// Layout: installations at <c>&lt;plugins&gt;/&lt;plugin-id&gt;/</c>; staging at
/// <c>&lt;plugins&gt;/.staging/&lt;operation&gt;/</c> (same volume, so directory moves are atomic
/// renames; the Loader ignores it because it has no <c>plugin.json</c>); records at
/// <c>&lt;packages&gt;/installed/&lt;plugin-id&gt;/</c> with <c>record.json</c>, the exact
/// <c>integrity.json</c> bytes, and <c>integrity.sig</c> for official packages.
/// </para>
/// <para>
/// Publishing writes a journal before each step (backup the current version, place the new one,
/// swap the record). <see cref="Recover"/> runs at daemon start and completes or rolls back an
/// interrupted operation, so an installation directory is never left half-written: either the
/// previous version or the new, fully verified version is in place.
/// </para>
/// </remarks>
public sealed class PackageStore
{
    private const string RecordFileName = "record.json";
    private const string JournalFileName = "journal.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly TrustStore _trustStore;
    private readonly PackageLimits _limits;

    public PackageStore(string pluginRoot, string packagesRoot, TrustStore trustStore, PackageLimits? limits = null)
    {
        PluginRoot = Path.GetFullPath(pluginRoot);
        PackagesRoot = Path.GetFullPath(packagesRoot);
        _trustStore = trustStore;
        _limits = limits ?? PackageLimits.Default;
        StagingRoot = Path.Combine(PluginRoot, ".staging");
        RecordsRoot = Path.Combine(PackagesRoot, "installed");
    }

    public string PluginRoot { get; }

    public string PackagesRoot { get; }

    public string StagingRoot { get; }

    public string RecordsRoot { get; }

    public TrustStore TrustStore => _trustStore;

    public PackageLimits Limits => _limits;

    /// <summary>Creates a fresh, empty operation directory in the staging area.</summary>
    public string CreateOperationDirectory(string operationId)
    {
        if (!TrustedKey.IsValidKeyId(operationId))
        {
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        }

        var directory = Path.Combine(StagingRoot, operationId);
        Directory.CreateDirectory(StagingRoot);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Deletes an operation directory (only below the staging root).</summary>
    public void DeleteOperationDirectory(string operationDirectory)
    {
        var full = Path.GetFullPath(operationDirectory);
        if (!full.StartsWith(StagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a directory outside the staging area.");
        }

        DeleteDirectoryBestEffort(full);
    }

    public string GetInstallationDirectory(PluginId pluginId) => Path.Combine(PluginRoot, pluginId.Value);

    public IReadOnlyList<InstalledPackageRecord> ListRecords()
    {
        if (!Directory.Exists(RecordsRoot))
        {
            return [];
        }

        var records = new List<InstalledPackageRecord>();
        foreach (var directory in Directory.GetDirectories(RecordsRoot))
        {
            if (Path.GetFileName(directory).StartsWith('.'))
            {
                continue;
            }

            if (TryReadRecord(directory) is { } record)
            {
                records.Add(record);
            }
        }

        return [.. records.OrderBy(r => r.PluginId, StringComparer.Ordinal)];
    }

    public InstalledPackageRecord? GetRecord(PluginId pluginId) => TryReadRecord(Path.Combine(RecordsRoot, pluginId.Value));

    /// <summary>The record whose installation directory is <paramref name="installationDirectory"/>, if any.</summary>
    public InstalledPackageRecord? FindRecordForDirectory(string installationDirectory)
    {
        var full = Path.GetFullPath(installationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), PluginRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = Path.GetFileName(full);
        return PluginId.TryParse(name, out var id) && GetRecord(id) is { } record &&
               string.Equals(record.Directory, name, StringComparison.OrdinalIgnoreCase)
            ? record
            : null;
    }

    /// <summary>
    /// Publishes a verified package as the installation of its plugin, replacing an existing managed
    /// installation atomically. The plugin must not be loaded (the caller unregisters it first).
    /// </summary>
    public InstalledPackageRecord Publish(string operationDirectory, VerifiedPackage package, PackageSource source, long? registryRevision)
    {
        ArgumentNullException.ThrowIfNull(package);
        var pluginId = package.PluginId;
        ValidateInstallDirectoryName(pluginId);
        var final = GetInstallationDirectory(pluginId);
        var existing = GetRecord(pluginId);
        if (existing is null && Directory.Exists(final))
        {
            throw new PackageException(PackageErrorCodes.InstallationConflict, $"'{final}' exists but is not a managed installation; remove it manually first.");
        }

        var record = new InstalledPackageRecord
        {
            PluginId = pluginId.Value,
            Version = package.Version.ToString(),
            Name = package.Candidate.Descriptor.Name,
            Author = package.Candidate.Author,
            Description = package.Candidate.Description,
            Directory = pluginId.Value,
            Source = source,
            Trust = package.Trust,
            SigningKeyId = package.SigningKey?.KeyId,
            SignedWithTestKey = package.SigningKey?.IsTestKey ?? false,
            PackageSha256 = package.PackageSha256,
            PackageSize = package.PackageSize,
            FileCount = package.Files.Count,
            RegistryRevision = registryRevision,
            InstalledAt = DateTimeOffset.UtcNow,
        };

        var journal = new Journal
        {
            Operation = "install",
            PluginId = pluginId.Value,
            Phase = "prepared",
            Record = record,
            Integrity = Convert.ToBase64String(package.IntegrityManifestBytes),
            Signature = package.SignatureBytes is null ? null : Convert.ToBase64String(package.SignatureBytes),
        };
        WriteJournal(operationDirectory, journal);

        var backup = Path.Combine(operationDirectory, "previous");
        if (Directory.Exists(final))
        {
            MoveDirectory(final, backup);
            WriteJournal(operationDirectory, journal with { Phase = "backed-up" });
        }

        try
        {
            MoveDirectory(package.ExtractedDirectory, final);
        }
        catch
        {
            // Put the previous version back; nothing else changed.
            if (Directory.Exists(backup) && !Directory.Exists(final))
            {
                MoveDirectory(backup, final);
            }

            WriteJournal(operationDirectory, journal with { Phase = "rolled-back" });
            throw;
        }

        WriteJournal(operationDirectory, journal with { Phase = "placed" });
        WriteRecordFiles(operationDirectory, journal);
        WriteJournal(operationDirectory, journal with { Phase = "committed" });
        DeleteOperationDirectory(operationDirectory);
        return record;
    }

    /// <summary>Removes a managed installation and its record. The plugin must not be loaded.</summary>
    public void Remove(string operationDirectory, PluginId pluginId)
    {
        var record = GetRecord(pluginId)
            ?? throw new PackageException(PackageErrorCodes.InstallationNotFound, $"'{pluginId}' is not a managed installation.");
        WriteJournal(operationDirectory, new Journal { Operation = "remove", PluginId = pluginId.Value, Phase = "removing", Record = record });

        var final = Path.Combine(PluginRoot, record.Directory);
        if (Directory.Exists(final))
        {
            // Moving first makes the removal atomic for the Loader and fails cleanly if files are in use.
            MoveDirectory(final, Path.Combine(operationDirectory, "removed"));
        }

        CompleteRemoval(operationDirectory, pluginId);
    }

    /// <summary>
    /// Completes or rolls back operations interrupted by a crash or power loss, and deletes stale
    /// staging data. Call once at startup, before plugins are discovered.
    /// </summary>
    public IReadOnlyList<string> Recover()
    {
        var messages = new List<string>();
        if (!Directory.Exists(StagingRoot))
        {
            return messages;
        }

        foreach (var operationDirectory in Directory.GetDirectories(StagingRoot))
        {
            var journal = ReadJournal(operationDirectory);
            try
            {
                if (journal is null)
                {
                    messages.Add($"Deleted stale staging data '{Path.GetFileName(operationDirectory)}'.");
                }
                else if (journal.Operation == "remove")
                {
                    var final = Path.Combine(PluginRoot, journal.Record.Directory);
                    if (Directory.Exists(final))
                    {
                        MoveDirectory(final, Path.Combine(operationDirectory, "removed-" + Guid.NewGuid().ToString("N")));
                    }

                    CompleteRemoval(operationDirectory, PluginId.Parse(journal.PluginId));
                    messages.Add($"Completed the interrupted removal of '{journal.PluginId}'.");
                    continue;
                }
                else
                {
                    var final = Path.Combine(PluginRoot, journal.Record.Directory);
                    var backup = Path.Combine(operationDirectory, "previous");
                    switch (journal.Phase)
                    {
                        case "placed":
                            WriteRecordFiles(operationDirectory, journal);
                            messages.Add($"Completed the interrupted installation of '{journal.PluginId}' {journal.Record.Version}.");
                            break;
                        case "backed-up":
                        case "rolled-back":
                            if (Directory.Exists(backup) && !Directory.Exists(final))
                            {
                                MoveDirectory(backup, final);
                            }

                            messages.Add($"Rolled back the interrupted installation of '{journal.PluginId}'; the previous version is kept.");
                            break;
                        case "committed":
                            break;
                        default:
                            messages.Add($"Discarded the interrupted installation of '{journal.PluginId}' (nothing had been changed).");
                            break;
                    }
                }

                DeleteOperationDirectory(operationDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                messages.Add($"Could not recover staging operation '{Path.GetFileName(operationDirectory)}': {exception.Message}");
            }
        }

        return messages;
    }

    /// <summary>
    /// Checks an installation now: the official signature of its recorded manifest (official
    /// packages), its identity, and that the directory contains exactly the listed files with
    /// matching sizes and SHA-256. Does not load anything.
    /// </summary>
    public InstallationVerification Verify(PluginId pluginId)
    {
        try
        {
            using var lease = VerifyAndLock(pluginId, descriptor: null);
            return new InstallationVerification(pluginId.Value, InstallationStatus.Verified, "Every file matches the recorded manifest.", [], DateTimeOffset.UtcNow);
        }
        catch (InstallationCheckException exception)
        {
            return new InstallationVerification(pluginId.Value, exception.Status, exception.Message, exception.Problems, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Verifies an installation and returns open read handles (no write or delete sharing) for every
    /// file, so the verified files cannot be replaced until the lease is disposed.
    /// </summary>
    /// <exception cref="InstallationCheckException">The installation does not verify.</exception>
    public IDisposable VerifyAndLock(PluginId pluginId, PluginDescriptor? descriptor)
    {
        var recordDirectory = Path.Combine(RecordsRoot, pluginId.Value);
        var record = TryReadRecord(recordDirectory)
            ?? throw new InstallationCheckException(InstallationStatus.Missing, $"'{pluginId}' has no installation record.", []);
        var root = Path.Combine(PluginRoot, record.Directory);
        if (!Directory.Exists(root))
        {
            throw new InstallationCheckException(InstallationStatus.Missing, $"The installation directory of '{pluginId}' is missing.", []);
        }

        byte[] manifestBytes;
        byte[]? signatureBytes = null;
        try
        {
            manifestBytes = File.ReadAllBytes(Path.Combine(recordDirectory, PackagePaths.IntegrityManifestName));
            var signaturePath = Path.Combine(recordDirectory, PackagePaths.IntegritySignatureName);
            if (File.Exists(signaturePath))
            {
                signatureBytes = File.ReadAllBytes(signaturePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallationCheckException(InstallationStatus.Missing, $"The recorded manifest of '{pluginId}' cannot be read: {exception.Message}", []);
        }

        if (record.Trust == PackageTrust.Official)
        {
            if (signatureBytes is null)
            {
                throw new InstallationCheckException(InstallationStatus.SignatureInvalid, $"The official signature of '{pluginId}' is missing.", []);
            }

            try
            {
                var envelope = SignatureEnvelope.Parse(signatureBytes, _limits.MaxSignatureBytes);
                _trustStore.Verify(manifestBytes, envelope, KeyPurposes.Packages);
            }
            catch (PackageException exception)
            {
                throw new InstallationCheckException(InstallationStatus.SignatureInvalid, exception.Message, []);
            }
        }

        IntegrityManifest manifest;
        try
        {
            manifest = IntegrityManifest.Parse(manifestBytes, _limits);
        }
        catch (PackageException exception)
        {
            throw new InstallationCheckException(InstallationStatus.Modified, exception.Message, []);
        }

        if (manifest.PluginId != pluginId || !string.Equals(manifest.Version.ToString(), record.Version, StringComparison.Ordinal))
        {
            throw new InstallationCheckException(InstallationStatus.Modified, $"The recorded manifest does not match '{pluginId}' {record.Version}.", []);
        }

        if (descriptor is not null && (descriptor.Id != pluginId || PluginVersions.Compare(descriptor.Version, manifest.Version) != 0))
        {
            throw new InstallationCheckException(InstallationStatus.Modified, $"plugin.json now declares '{descriptor.Id}' {descriptor.Version}, not the installed '{pluginId}' {manifest.Version}.", []);
        }

        return InstallationLock.Acquire(root, manifest);
    }

    private void CompleteRemoval(string operationDirectory, PluginId pluginId)
    {
        var recordDirectory = Path.Combine(RecordsRoot, pluginId.Value);
        if (Directory.Exists(recordDirectory))
        {
            MoveDirectory(recordDirectory, Path.Combine(operationDirectory, "removed-record-" + Guid.NewGuid().ToString("N")));
        }

        DeleteOperationDirectory(operationDirectory);
    }

    private void WriteRecordFiles(string operationDirectory, Journal journal)
    {
        Directory.CreateDirectory(RecordsRoot);
        var temporary = Path.Combine(RecordsRoot, ".tmp-" + Path.GetFileName(operationDirectory));
        DeleteDirectoryBestEffort(temporary);
        Directory.CreateDirectory(temporary);
        WriteAllBytesDurable(Path.Combine(temporary, PackagePaths.IntegrityManifestName), Convert.FromBase64String(journal.Integrity!));
        if (journal.Signature is not null)
        {
            WriteAllBytesDurable(Path.Combine(temporary, PackagePaths.IntegritySignatureName), Convert.FromBase64String(journal.Signature));
        }

        WriteAllBytesDurable(Path.Combine(temporary, RecordFileName), JsonSerializer.SerializeToUtf8Bytes(journal.Record, JsonOptions));

        var recordDirectory = Path.Combine(RecordsRoot, journal.PluginId);
        if (Directory.Exists(recordDirectory))
        {
            MoveDirectory(recordDirectory, Path.Combine(operationDirectory, "previous-record-" + Guid.NewGuid().ToString("N")));
        }

        MoveDirectory(temporary, recordDirectory);
    }

    private static InstalledPackageRecord? TryReadRecord(string recordDirectory)
    {
        var path = Path.Combine(recordDirectory, RecordFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<InstalledPackageRecord>(File.ReadAllBytes(path), JsonOptions);
            return record is not null && PluginId.IsValid(record.PluginId) && record.Directory == record.PluginId ? record : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ValidateInstallDirectoryName(PluginId pluginId)
    {
        if (!PackagePaths.TryValidate(pluginId.Value, PackageLimits.Default, out var reason))
        {
            throw new PackageException(PackageErrorCodes.InstallationConflict, $"Plugin ID '{pluginId}' cannot be used as a Windows directory name: {reason}.");
        }
    }

    /// <summary>Test hook: called after each journal phase is durably written (simulates crashes).</summary>
    internal Action<string>? AfterJournalPhase { get; set; }

    private void WriteJournal(string operationDirectory, Journal journal)
    {
        var path = Path.Combine(operationDirectory, JournalFileName);
        var temporary = path + ".tmp";
        WriteAllBytesDurable(temporary, JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions));
        File.Move(temporary, path, overwrite: true);
        AfterJournalPhase?.Invoke(journal.Phase);
    }

    private static Journal? ReadJournal(string operationDirectory)
    {
        var path = Path.Combine(operationDirectory, JournalFileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path), JsonOptions) : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteAllBytesDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Directory rename with short retries (antivirus scanners and the indexer open files briefly).</summary>
    internal static void MoveDirectory(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(100 * attempt);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new PackageException(PackageErrorCodes.FilesInUse, $"'{source}' could not be moved; its files are in use ({exception.Message}).");
            }
        }
    }

    internal static void DeleteDirectoryBestEffort(string directory)
    {
        for (var attempt = 1; attempt <= 5 && Directory.Exists(directory); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private sealed record Journal
    {
        public int SchemaVersion { get; init; } = 1;

        public required string Operation { get; init; }

        public required string PluginId { get; init; }

        public required string Phase { get; init; }

        public required InstalledPackageRecord Record { get; init; }

        public string? Integrity { get; init; }

        public string? Signature { get; init; }
    }
}

/// <summary>An installation failed verification.</summary>
public sealed class InstallationCheckException : Exception
{
    public InstallationCheckException(InstallationStatus status, string message, IReadOnlyList<string> problems)
        : base(message)
    {
        Status = status;
        Problems = problems;
    }

    public InstallationCheckException()
        : this(InstallationStatus.Missing, "The installation could not be verified.", [])
    {
    }

    public InstallationCheckException(string message)
        : this(InstallationStatus.Modified, message, [])
    {
    }

    public InstallationCheckException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [];
    }

    public InstallationStatus Status { get; }

    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// Open handles on every file of a verified installation. Files are opened for reading with read
/// sharing only, hashed through the same handle, and kept open: until disposal no process can
/// write, replace, rename, or delete them (or rename their directories).
/// </summary>
internal sealed class InstallationLock : IDisposable
{
    private readonly List<FileStream> _handles;

    private InstallationLock(List<FileStream> handles) => _handles = handles;

    public static InstallationLock Acquire(string root, IntegrityManifest manifest)
    {
        var handles = new List<FileStream>();
        var problems = new List<string>();
        try
        {
            var expected = manifest.Files.ToDictionary(f => PackagePaths.ComparisonKey(f.Path), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rootInfo = new DirectoryInfo(root);
            if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InstallationCheckException(InstallationStatus.Modified, "The installation directory is a reparse point.", ["(root)"]);
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
            foreach (var entry in rootInfo.EnumerateFileSystemInfos("*", options))
            {
                var relative = PackagePaths.ToPackagePath(root, entry.FullName);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    problems.Add($"{relative}: reparse point or symbolic link");
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    continue;
                }

                var key = PackagePaths.ComparisonKey(relative);
                if (!expected.TryGetValue(key, out var file))
                {
                    problems.Add($"{relative}: not part of the package");
                    continue;
                }

                seen.Add(key);
                var handle = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                handles.Add(handle);
                if (handle.Length != file.Size)
                {
                    problems.Add($"{file.Path}: size {handle.Length}, expected {file.Size}");
                    continue;
                }

                var actual = Sha256Hex.Compute(handle);
                handle.Position = 0;
                if (!Sha256Hex.Equal(actual, file.Sha256))
                {
                    problems.Add($"{file.Path}: SHA-256 differs");
                }
            }

            foreach (var file in manifest.Files)
            {
                if (!seen.Contains(PackagePaths.ComparisonKey(file.Path)))
                {
                    problems.Add($"{file.Path}: missing");
                }
            }

            if (problems.Count > 0)
            {
                throw new InstallationCheckException(
                    InstallationStatus.Modified,
                    $"The installation of '{manifest.PluginId}' was modified: {problems[0]}{(problems.Count > 1 ? $" (and {problems.Count - 1} more)" : string.Empty)}.",
                    problems);
            }

            return new InstallationLock(handles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DisposeAll(handles);
            throw new InstallationCheckException(InstallationStatus.Modified, $"The installation could not be read for verification: {exception.Message}", problems);
        }
        catch
        {
            DisposeAll(handles);
            throw;
        }
    }

    public void Dispose() => DisposeAll(_handles);

    private static void DisposeAll(List<FileStream> handles)
    {
        foreach (var handle in handles)
        {
            handle.Dispose();
        }

        handles.Clear();
    }
}
