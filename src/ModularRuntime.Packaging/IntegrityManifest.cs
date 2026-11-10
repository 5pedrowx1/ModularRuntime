using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModularRuntime.Contracts;

namespace ModularRuntime.Packaging;

/// <summary>One content file of a package: path, size, and SHA-256.</summary>
public sealed record IntegrityFile(string Path, long Size, string Sha256);

/// <summary>
/// The list of every content file of a package (<c>integrity.json</c>). Official packages sign its
/// exact bytes; the signature therefore covers the plugin ID, version, and every file's hash.
/// </summary>
/// <remarks>
/// <para>
/// Format (schema version 1): UTF-8 JSON without BOM; properties <c>schemaVersion</c>,
/// <c>pluginId</c>, <c>version</c>, <c>hashAlgorithm</c> (<c>SHA-256</c>), and <c>files</c>, each
/// file with <c>path</c>, <c>size</c>, and <c>sha256</c> (64 lowercase hex characters). Paths follow
/// <see cref="PackagePaths"/>. Every content file appears exactly once; <c>integrity.json</c> and
/// <c>integrity.sig</c> are metadata and never appear. Unknown or duplicate properties are rejected.
/// </para>
/// <para>
/// <see cref="Serialize"/> is deterministic: files sorted by ordinal path, two-space indentation,
/// LF line endings, no trailing newline. Verification never re-serializes: the signature is checked
/// over the bytes as they are in the package, then those bytes are parsed.
/// </para>
/// </remarks>
public sealed record IntegrityManifest(int SchemaVersion, PluginId PluginId, Version Version, IReadOnlyList<IntegrityFile> Files)
{
    public const int CurrentSchemaVersion = 1;
    public const string HashAlgorithm = "SHA-256";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>Creates a manifest for a set of files, sorted deterministically.</summary>
    public static IntegrityManifest Create(PluginId pluginId, Version version, IEnumerable<IntegrityFile> files) =>
        new(CurrentSchemaVersion, pluginId, version, [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)]);

    /// <summary>Deterministic UTF-8 bytes of this manifest.</summary>
    public byte[] Serialize()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("pluginId", PluginId.Value);
            writer.WriteString("version", Version.ToString());
            writer.WriteString("hashAlgorithm", HashAlgorithm);
            writer.WriteStartArray("files");
            foreach (var file in Files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteNumber("size", file.Size);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Parses and validates manifest bytes; throws <see cref="PackageException"/>.</summary>
    public static IntegrityManifest Parse(ReadOnlySpan<byte> bytes, PackageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (bytes.Length > limits.MaxManifestBytes)
        {
            throw Invalid($"integrity.json exceeds {limits.MaxManifestBytes} bytes");
        }

        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            throw Invalid("integrity.json must not start with a byte order mark");
        }

        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(bytes, ReadOptions);
        }
        catch (JsonException exception)
        {
            throw Invalid($"integrity.json is not valid: {exception.Message}");
        }

        if (document is null)
        {
            throw Invalid("integrity.json is empty");
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw Invalid($"unsupported schemaVersion {document.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "(missing)"}");
        }

        if (!PluginId.TryParse(document.PluginId, out var pluginId))
        {
            throw Invalid("pluginId is missing or invalid");
        }

        if (!PluginVersions.TryParse(document.Version, out var version))
        {
            throw Invalid("version is missing or invalid (2 to 4 numeric components)");
        }

        if (!string.Equals(document.HashAlgorithm, HashAlgorithm, StringComparison.Ordinal))
        {
            throw Invalid($"hashAlgorithm must be '{HashAlgorithm}'");
        }

        if (document.Files is not { Count: > 0 })
        {
            throw Invalid("files is missing or empty");
        }

        if (document.Files.Count > limits.MaxEntries)
        {
            throw Invalid($"more than {limits.MaxEntries} files");
        }

        var files = new List<IntegrityFile>(document.Files.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Files)
        {
            if (entry is null || entry.Path is null || entry.Size is null || entry.Sha256 is null)
            {
                throw Invalid("every file needs path, size, and sha256");
            }

            if (!PackagePaths.TryValidate(entry.Path, limits, out var reason))
            {
                throw Invalid($"file path '{PackagePaths.Describe(entry.Path)}' is unsafe: {reason}");
            }

            if (PackagePaths.IsMetadataFile(entry.Path))
            {
                throw Invalid($"'{entry.Path}' is signature metadata and must not be listed");
            }

            if (!keys.Add(PackagePaths.ComparisonKey(entry.Path)))
            {
                throw Invalid($"'{entry.Path}' is listed more than once (paths are case-insensitive)");
            }

            if (entry.Size < 0 || entry.Size > limits.MaxFileBytes)
            {
                throw Invalid($"'{entry.Path}' has an invalid size");
            }

            if (!Sha256Hex.IsValid(entry.Sha256))
            {
                throw Invalid($"'{entry.Path}' has an invalid sha256 (64 lowercase hexadecimal characters)");
            }

            files.Add(new IntegrityFile(entry.Path, entry.Size.Value, entry.Sha256));
        }

        if (!keys.Contains(PackagePaths.ComparisonKey(PackagePaths.PluginManifestName)))
        {
            throw Invalid("plugin.json must be listed");
        }

        return new IntegrityManifest(CurrentSchemaVersion, pluginId, version, files);
    }

    private static PackageException Invalid(string message) =>
        new(PackageErrorCodes.IntegrityManifestInvalid, $"Invalid integrity manifest: {message}.");

    private sealed class Document
    {
        [JsonPropertyName("schemaVersion")]
        public int? SchemaVersion { get; init; }

        [JsonPropertyName("pluginId")]
        public string? PluginId { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("hashAlgorithm")]
        public string? HashAlgorithm { get; init; }

        [JsonPropertyName("files")]
        public List<FileDocument?>? Files { get; init; }
    }

    private sealed class FileDocument
    {
        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("size")]
        public long? Size { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }
    }
}

/// <summary>Lowercase hexadecimal SHA-256 helpers.</summary>
public static class Sha256Hex
{
    public static bool IsValid(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static string Compute(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Compute(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    public static async Task<string> ComputeFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Constant-time comparison of two hex digests.</summary>
    public static bool Equal(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}

/// <summary>Plugin version rules shared with <c>plugin.json</c>: 2 to 4 numeric components.</summary>
public static class PluginVersions
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrEmpty(text) || text.Length > 64)
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length is < 2 or > 4 || parts.Any(p => p.Length is 0 or > 9 || !p.All(char.IsAsciiDigit)))
        {
            return false;
        }

        return Version.TryParse(text, out version!);
    }

    /// <summary>Compares two versions with missing components treated as zero (1.0 equals 1.0.0).</summary>
    public static int Compare(Version a, Version b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Normalize(a).CompareTo(Normalize(b));
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
