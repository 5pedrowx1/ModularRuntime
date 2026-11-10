using System.Globalization;
using System.Text.Json;
using ModularRuntime.Contracts;

namespace ModularRuntime.Loader.Internal;

internal sealed record ManifestReadOutcome(
    PluginCandidate? Candidate,
    IReadOnlyList<PluginDiscoveryError> Errors);

internal static class PluginManifestReader
{
    private const long MaxManifestBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<ManifestReadOutcome> ReadAsync(
        string pluginDirectory,
        string manifestPath,
        CancellationToken cancellationToken,
        bool requireEntryAssemblyFile = true)
    {
        var errors = new List<PluginDiscoveryError>();

        PluginManifestDocument? manifest;
        try
        {
            var file = new FileInfo(manifestPath);
            if (!file.Exists)
            {
                errors.Add(Error(
                    PluginDiscoveryErrorCodes.ManifestReadFailed,
                    $"Manifest '{manifestPath}' does not exist.",
                    pluginDirectory,
                    manifestPath));
                return new ManifestReadOutcome(null, errors);
            }

            if (file.Length > MaxManifestBytes)
            {
                errors.Add(Error(
                    PluginDiscoveryErrorCodes.ManifestReadFailed,
                    $"Manifest '{manifestPath}' exceeds the {MaxManifestBytes} byte limit.",
                    pluginDirectory,
                    manifestPath));
                return new ManifestReadOutcome(null, errors);
            }

            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            manifest = await JsonSerializer.DeserializeAsync<PluginManifestDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            errors.Add(Error(
                PluginDiscoveryErrorCodes.ManifestJsonInvalid,
                $"Manifest '{manifestPath}' is not valid JSON: {exception.Message}",
                pluginDirectory,
                manifestPath,
                exception: exception));
            return new ManifestReadOutcome(null, errors);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            errors.Add(Error(
                PluginDiscoveryErrorCodes.ManifestReadFailed,
                $"Manifest '{manifestPath}' could not be read: {exception.Message}",
                pluginDirectory,
                manifestPath,
                exception: exception));
            return new ManifestReadOutcome(null, errors);
        }

        if (manifest is null)
        {
            errors.Add(Error(
                PluginDiscoveryErrorCodes.ManifestJsonInvalid,
                $"Manifest '{manifestPath}' contains a null JSON value.",
                pluginDirectory,
                manifestPath));
            return new ManifestReadOutcome(null, errors);
        }

        PluginId? pluginId = null;
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldMissing,
                "id",
                $"Manifest '{manifestPath}' is missing required field 'id'.",
                pluginDirectory,
                manifestPath));
        }
        else if (!PluginId.TryParse(manifest.Id, out var parsedId))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldInvalid,
                "id",
                $"Manifest '{manifestPath}' has invalid plugin ID '{manifest.Id}'.",
                pluginDirectory,
                manifestPath));
        }
        else
        {
            pluginId = parsedId;
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldMissing,
                "name",
                $"Manifest '{manifestPath}' is missing required field 'name'.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }
        else if (manifest.Name.Length > 200)
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldInvalid,
                "name",
                $"Manifest '{manifestPath}' has a name longer than 200 characters.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }

        Version? pluginVersion = null;
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldMissing,
                "version",
                $"Manifest '{manifestPath}' is missing required field 'version'.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }
        else if (!TryParsePluginVersion(manifest.Version, out pluginVersion))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldInvalid,
                "version",
                $"Manifest '{manifestPath}' has invalid numeric version '{manifest.Version}'. " +
                "Use two to four numeric components, for example '1.0.0'.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }

        Version? apiVersion = null;
        if (string.IsNullOrWhiteSpace(manifest.ApiVersion))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldMissing,
                "apiVersion",
                $"Manifest '{manifestPath}' is missing required field 'apiVersion'.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }
        else if (!TryParseApiVersion(manifest.ApiVersion, out apiVersion))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldInvalid,
                "apiVersion",
                $"Manifest '{manifestPath}' has invalid API version '{manifest.ApiVersion}'. " +
                $"Use 'major.minor', for example '{PluginApiVersion.Supported}'.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }
        else if (!PluginApiVersion.IsCompatible(apiVersion!))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ApiVersionIncompatible,
                "apiVersion",
                $"Plugin '{pluginId}' requires API version {apiVersion}, but this runtime supports " +
                $"API version {PluginApiVersion.Supported}.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }

        string? entryAssemblyPath = null;
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldMissing,
                "entryAssembly",
                $"Manifest '{manifestPath}' is missing required field 'entryAssembly'.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }
        else if (!PluginPathValidator.TryResolveEntryAssembly(
                     pluginDirectory,
                     manifest.EntryAssembly,
                     out var resolvedAssembly,
                     out var pathError))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.EntryAssemblyPathInvalid,
                "entryAssembly",
                $"Manifest '{manifestPath}' has an unsafe entry assembly path: {pathError}",
                pluginDirectory,
                manifestPath,
                pluginId));
        }
        else if (requireEntryAssemblyFile && !File.Exists(resolvedAssembly))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.EntryAssemblyMissing,
                "entryAssembly",
                $"Plugin '{pluginId}' declares entry assembly '{manifest.EntryAssembly}', but " +
                $"'{resolvedAssembly}' does not exist.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }
        else
        {
            entryAssemblyPath = resolvedAssembly;
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldMissing,
                "entryType",
                $"Manifest '{manifestPath}' is missing required field 'entryType'.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }
        else if (!IsValidTypeName(manifest.EntryType))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldInvalid,
                "entryType",
                $"Manifest '{manifestPath}' has invalid entry type '{manifest.EntryType}'.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }

        var startOrder = manifest.StartOrder ?? 0;
        if (startOrder is < PluginDescriptor.MinStartOrder or > PluginDescriptor.MaxStartOrder)
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldInvalid,
                "startOrder",
                $"Manifest '{manifestPath}' has start order {startOrder}; it must be between " +
                $"{PluginDescriptor.MinStartOrder} and {PluginDescriptor.MaxStartOrder}.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }

        ValidateOptionalText(manifest.Description, "description", 1000, manifestPath, pluginDirectory, pluginId, errors);
        ValidateOptionalText(manifest.Author, "author", 200, manifestPath, pluginDirectory, pluginId, errors);

        if (errors.Count > 0)
        {
            return new ManifestReadOutcome(null, errors);
        }

        PluginDescriptor descriptor;
        try
        {
            descriptor = new PluginDescriptor(
                pluginId!.Value,
                manifest.Name!,
                pluginVersion!,
                apiVersion!,
                manifest.EntryAssembly!,
                manifest.EntryType!,
                startOrder);
        }
        catch (ArgumentException exception)
        {
            errors.Add(Error(
                PluginDiscoveryErrorCodes.ManifestFieldInvalid,
                $"Manifest '{manifestPath}' produced an invalid descriptor: {exception.Message}",
                pluginDirectory,
                manifestPath,
                pluginId,
                exception));
            return new ManifestReadOutcome(null, errors);
        }

        var candidate = new PluginCandidate(
            descriptor,
            pluginDirectory,
            manifestPath,
            entryAssemblyPath!)
        {
            Description = string.IsNullOrWhiteSpace(manifest.Description) ? null : manifest.Description.Trim(),
            Author = string.IsNullOrWhiteSpace(manifest.Author) ? null : manifest.Author.Trim(),
        };
        return new ManifestReadOutcome(candidate, errors);
    }

    private static bool TryParsePluginVersion(string text, out Version? version)
    {
        version = null;
        var components = text.Split('.');
        if (components.Length is < 2 or > 4 ||
            components.Any(component =>
                component.Length == 0 || !component.All(char.IsAsciiDigit)))
        {
            return false;
        }

        if (!Version.TryParse(text, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    private static bool TryParseApiVersion(string text, out Version? version)
    {
        version = null;
        var components = text.Split('.');
        if (components.Length != 2 ||
            !int.TryParse(components[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        version = new Version(major, minor);
        return true;
    }

    private static bool IsValidTypeName(string typeName) =>
        typeName.Length <= 512
        && !typeName.Any(char.IsWhiteSpace)
        && !typeName.Contains(',')
        && !typeName.StartsWith('.')
        && !typeName.EndsWith('.');

    private static void ValidateOptionalText(
        string? value,
        string field,
        int maxLength,
        string manifestPath,
        string pluginDirectory,
        PluginId? pluginId,
        List<PluginDiscoveryError> errors)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > maxLength || value.Any(c => char.IsControl(c) && c is not '\n'))
        {
            errors.Add(FieldError(
                PluginDiscoveryErrorCodes.ManifestFieldInvalid,
                field,
                $"Manifest '{manifestPath}' has an invalid '{field}': at most {maxLength} characters and no control characters.",
                pluginDirectory,
                manifestPath,
                pluginId));
        }
    }

    private static PluginDiscoveryError FieldError(
        string code,
        string field,
        string message,
        string pluginDirectory,
        string manifestPath,
        PluginId? pluginId = null,
        Exception? exception = null) =>
        new(
            code,
            message,
            pluginDirectory,
            manifestPath,
            pluginId?.Value,
            field,
            PluginDiagnostic.FromExceptionOrNull(exception));

    private static PluginDiscoveryError Error(
        string code,
        string message,
        string pluginDirectory,
        string? manifestPath = null,
        PluginId? pluginId = null,
        Exception? exception = null) =>
        new(
            code,
            message,
            pluginDirectory,
            manifestPath,
            pluginId?.Value,
            Field: null,
            PluginDiagnostic.FromExceptionOrNull(exception));
}
