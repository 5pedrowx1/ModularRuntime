using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ModularRuntime.Contracts;

namespace ModularRuntime.Sdk;

/// <summary>
/// Converts between your own types and <see cref="JsonPayload"/>, the neutral representation
/// used at every boundary between plugins (events, service calls, extension contributions).
/// </summary>
/// <remarks>
/// Only JSON text crosses the boundary: the receiver deserializes into its own types, loaded in
/// its own load context. Never pass objects of your plugin to another plugin directly.
/// </remarks>
public static class PluginJson
{
    /// <summary>
    /// Default options: web defaults (camelCase property names, case-insensitive reading, numbers
    /// as numbers). Pass your own <see cref="JsonSerializerOptions"/> (or source-generated options)
    /// to override.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    /// <summary>Serializes <paramref name="value"/> to a payload.</summary>
    public static JsonPayload ToPayload<T>(T value, JsonSerializerOptions? options = null) =>
        JsonPayload.Parse(JsonSerializer.Serialize(value, options ?? DefaultOptions));

    /// <summary>
    /// Deserializes a payload. Throws <see cref="PluginPayloadException"/> when the JSON does not
    /// match <typeparamref name="T"/>.
    /// </summary>
    public static T? FromPayload<T>(JsonPayload payload, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return TryFromPayload<T>(payload, out var value, out var error, options)
            ? value
            : throw new PluginPayloadException(error);
    }

    /// <summary>Deserializes a payload without throwing on invalid content.</summary>
    public static bool TryFromPayload<T>(
        JsonPayload payload,
        out T? value,
        [NotNullWhen(false)] out string? error,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            value = JsonSerializer.Deserialize<T>(payload.Json, options ?? DefaultOptions);
            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            value = default;
            error = $"The payload is not a valid {typeof(T).Name}: {exception.Message}";
            return false;
        }
        catch (NotSupportedException exception)
        {
            value = default;
            error = $"{typeof(T).Name} cannot be deserialized: {exception.Message}";
            return false;
        }
    }

    /// <summary>Deserializes this payload; see <see cref="FromPayload{T}"/>.</summary>
    public static T? As<T>(this JsonPayload payload, JsonSerializerOptions? options = null) => FromPayload<T>(payload, options);

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>A payload could not be converted to the requested type.</summary>
public sealed class PluginPayloadException : Exception
{
    public PluginPayloadException()
    {
    }

    public PluginPayloadException(string message)
        : base(message)
    {
    }

    public PluginPayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
