using System.Collections.ObjectModel;
using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Internal;

/// <summary>Validation and defensive copying of caller-supplied definitions.</summary>
internal static class Validation
{
    public const int MaxMetadataEntries = 32;
    public const int MaxMetadataKeyLength = 64;
    public const int MaxMetadataValueLength = 1024;
    public const int MaxOperations = 64;
    public const int MaxDisplayNameLength = 200;
    public const int MaxCorrelationIdLength = 128;

    public static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    /// <summary>
    /// Returns an immutable copy so that later mutation of the caller's dictionary cannot change
    /// published metadata, or <see langword="null"/> with an error message.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? TryCopyMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        out string? error)
    {
        error = null;
        if (metadata is null || metadata.Count == 0)
        {
            return EmptyMetadata;
        }

        if (metadata.Count > MaxMetadataEntries)
        {
            error = $"Metadata cannot contain more than {MaxMetadataEntries} entries.";
            return null;
        }

        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxMetadataKeyLength || key.Any(char.IsControl))
            {
                error = $"Metadata key '{key}' is invalid (1-{MaxMetadataKeyLength} printable characters).";
                return null;
            }

            if (value is null || value.Length > MaxMetadataValueLength)
            {
                error = $"Metadata value for '{key}' is null or longer than {MaxMetadataValueLength} characters.";
                return null;
            }

            copy[key] = value;
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    public static IReadOnlyDictionary<string, string> CopyMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        string parameterName) =>
        TryCopyMetadata(metadata, out var error) ?? throw new ArgumentException(error, parameterName);

    public static void ValidateDisplayName(string? name, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxDisplayNameLength)
        {
            throw new ArgumentException(
                $"A display name of 1-{MaxDisplayNameLength} characters is required.",
                parameterName);
        }
    }

    public static IReadOnlyList<string> CopyOperations(
        IReadOnlyList<string>? operations,
        bool allowEmpty,
        string parameterName)
    {
        if (operations is null || operations.Count == 0)
        {
            return allowEmpty
                ? []
                : throw new ArgumentException("At least one operation is required.", parameterName);
        }

        if (operations.Count > MaxOperations)
        {
            throw new ArgumentException($"At most {MaxOperations} operations are allowed.", parameterName);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            RuntimeName.Validate(operation, parameterName);
            if (!seen.Add(operation))
            {
                throw new ArgumentException($"Operation '{operation}' is declared more than once.", parameterName);
            }
        }

        return Array.AsReadOnly(operations.ToArray());
    }

    public static void ValidateVersion(Version? version, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(version, parameterName);
        if (version.Major < 0 || version.Minor < 0)
        {
            throw new ArgumentException("A version with major and minor components is required.", parameterName);
        }
    }

    /// <summary>Same major and <paramref name="available"/> &gt;= <paramref name="required"/>.</summary>
    public static bool IsVersionCompatible(Version available, Version required) =>
        available.Major == required.Major && Normalize(available) >= Normalize(required);

    // Version(1, 2) compares lower than Version(1, 2, 0); treat missing components as zero.
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    /// <summary>Same major and contribution minor &lt;= point minor.</summary>
    public static bool IsContractCompatible(Version point, Version contribution) =>
        point.Major == contribution.Major && contribution.Minor <= point.Minor;
}
