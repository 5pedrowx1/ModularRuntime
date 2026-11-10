using System.Text;

namespace ModularRuntime.Packaging;

/// <summary>
/// Rules for package-relative paths. They are deliberately stricter than what Windows accepts so a
/// path means exactly one file on every Windows file system and can never leave the installation.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Separator is <c>/</c> only; a backslash is rejected (it would be a separator on Windows).</item>
/// <item>No leading <c>/</c>, no drive letters, no <c>:</c> anywhere (also rules out alternate data
/// streams such as <c>file.dll:stream</c>), no UNC or device prefixes (<c>//</c>, <c>\\?\</c>).</item>
/// <item>Segments are non-empty, not <c>.</c> or <c>..</c>, contain no control characters or any of
/// <c>&lt;&gt;:"|?*\</c>, do not end with a dot or a space, and are not DOS device names
/// (<c>CON</c>, <c>NUL</c>, <c>COM1</c>, <c>LPT1</c>, ... with or without an extension).</item>
/// <item>The path must be in Unicode normalization form C.</item>
/// <item>Two paths are the same file when their <see cref="ComparisonKey"/> values are equal
/// (ordinal, upper invariant): Windows file systems are case-insensitive.</item>
/// </list>
/// </remarks>
public static class PackagePaths
{
    /// <summary>Signed integrity manifest at the package root; not listed in itself.</summary>
    public const string IntegrityManifestName = "integrity.json";

    /// <summary>Signature of the integrity manifest; not listed in the manifest.</summary>
    public const string IntegritySignatureName = "integrity.sig";

    public const string PluginManifestName = "plugin.json";

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private static readonly System.Buffers.SearchValues<char> InvalidSegmentChars = System.Buffers.SearchValues.Create("<>:\"|?*\\");

    /// <summary>Case-insensitive identity of a validated path.</summary>
    public static string ComparisonKey(string path) => path.ToUpperInvariant();

    /// <summary>True for the two signature-metadata files that are excluded from the file list.</summary>
    public static bool IsMetadataFile(string path) =>
        string.Equals(path, IntegrityManifestName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, IntegritySignatureName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates a file path; throws <see cref="PackageException"/> (<c>unsafe_entry_path</c>).</summary>
    public static void Validate(string path, PackageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (!TryValidate(path, limits, out var reason))
        {
            throw new PackageException(PackageErrorCodes.UnsafeEntryPath, $"Unsafe path '{Describe(path)}': {reason}.", path);
        }
    }

    public static bool TryValidate(string? path, PackageLimits limits, out string reason)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (string.IsNullOrEmpty(path))
        {
            reason = "empty path";
            return false;
        }

        if (path.Length > limits.MaxPathLength)
        {
            reason = $"longer than {limits.MaxPathLength} characters";
            return false;
        }

        if (!path.IsNormalized(NormalizationForm.FormC))
        {
            reason = "not in Unicode normalization form C";
            return false;
        }

        if (path.Contains('\\', StringComparison.Ordinal))
        {
            reason = "backslash separators are not allowed; use '/'";
            return false;
        }

        if (path.StartsWith('/'))
        {
            reason = "absolute or UNC path";
            return false;
        }

        var segments = path.Split('/');
        if (segments.Length > limits.MaxDepth)
        {
            reason = $"deeper than {limits.MaxDepth} levels";
            return false;
        }

        foreach (var segment in segments)
        {
            if (!TryValidateSegment(segment, out reason))
            {
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Resolves a validated path under <paramref name="root"/> and checks containment.</summary>
    public static string Resolve(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageException(PackageErrorCodes.UnsafeEntryPath, $"Path '{Describe(path)}' resolves outside the installation.", path);
        }

        return fullPath;
    }

    /// <summary>The package-relative path of a file below <paramref name="root"/>.</summary>
    public static string ToPackagePath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>A printable form of an untrusted path for messages (control characters escaped).</summary>
    public static string Describe(string? path)
    {
        if (path is null)
        {
            return "<null>";
        }

        var builder = new StringBuilder(Math.Min(path.Length, 120));
        foreach (var c in path.Length > 120 ? path[..120] : path)
        {
            builder.Append(char.IsControl(c) ? $"\\u{(int)c:x4}" : c);
        }

        return path.Length > 120 ? builder.Append('…').ToString() : builder.ToString();
    }

    private static bool TryValidateSegment(string segment, out string reason)
    {
        if (segment.Length == 0)
        {
            reason = "empty path segment (leading, trailing, or repeated '/')";
            return false;
        }

        if (segment is "." or "..")
        {
            reason = "relative segments '.' and '..' are not allowed";
            return false;
        }

        if (segment.Length > 255)
        {
            reason = "path segment longer than 255 characters";
            return false;
        }

        foreach (var c in segment)
        {
            if (c < 0x20 || c == 0x7F)
            {
                reason = "control characters are not allowed";
                return false;
            }
        }

        if (segment.IndexOfAny(InvalidSegmentChars) >= 0)
        {
            reason = "characters < > : \" | ? * \\ are not allowed (':' would also name an alternate data stream or a drive)";
            return false;
        }

        if (segment.EndsWith('.') || segment.EndsWith(' '))
        {
            reason = "segments cannot end with '.' or a space";
            return false;
        }

        var stem = segment.Split('.')[0].TrimEnd(' ');
        if (DeviceNames.Contains(stem))
        {
            reason = $"'{stem}' is a reserved Windows device name";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
