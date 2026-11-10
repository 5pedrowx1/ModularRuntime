using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModularRuntime.Contracts;

/// <summary>
/// Immutable, validated JSON text exchanged through events, services, and extensions.
/// </summary>
/// <remarks>
/// <para>
/// Payloads are the only data that crosses plugin boundaries. They are plain text, so
/// a payload never references a type, delegate, or object from a plugin assembly and
/// can be kept by any party without preventing a plugin's load context from unloading.
/// </para>
/// <para>
/// Construction validates JSON syntax (maximum depth <see cref="MaxDepth"/>, no comments,
/// no trailing commas). Size limits are enforced by the runtime when a payload is
/// published or returned (default 256 KiB). The runtime never deserializes payloads
/// into CLR types; consumers parse them explicitly with <see cref="ToNode"/> or
/// <see cref="ToDocument"/>.
/// </para>
/// </remarks>
public sealed class JsonPayload : IEquatable<JsonPayload>
{
    /// <summary>Maximum JSON nesting depth accepted by the runtime.</summary>
    public const int MaxDepth = 64;

    private static readonly JsonDocumentOptions ValidationOptions = new()
    {
        MaxDepth = MaxDepth,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    private JsonPayload(string json)
    {
        Json = json;
        ByteCount = Encoding.UTF8.GetByteCount(json);
    }

    /// <summary>The JSON literal <c>null</c>.</summary>
    public static JsonPayload Null { get; } = new("null");

    /// <summary>An empty JSON object, <c>{}</c>.</summary>
    public static JsonPayload EmptyObject { get; } = new("{}");

    /// <summary>The validated JSON text.</summary>
    public string Json { get; }

    /// <summary>Size of <see cref="Json"/> encoded as UTF-8, used for runtime limits.</summary>
    public int ByteCount { get; }

    /// <summary>Parses and validates JSON text.</summary>
    /// <exception cref="FormatException">The text is not valid JSON.</exception>
    public static JsonPayload Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return TryParse(json, out var payload, out var error)
            ? payload
            : throw new FormatException(error);
    }

    public static bool TryParse(string? json, [NotNullWhen(true)] out JsonPayload? payload) =>
        TryParse(json, out payload, out _);

    public static bool TryParse(
        string? json,
        [NotNullWhen(true)] out JsonPayload? payload,
        [NotNullWhen(false)] out string? error)
    {
        payload = null;
        if (json is null)
        {
            error = "JSON payload text is null.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, ValidationOptions);
        }
        catch (JsonException exception)
        {
            error = $"Invalid JSON payload: {exception.Message}";
            return false;
        }

        payload = new JsonPayload(json);
        error = null;
        return true;
    }

    /// <summary>Creates a payload from a JSON node (<see langword="null"/> becomes <see cref="Null"/>).</summary>
    public static JsonPayload FromNode(JsonNode? node) =>
        node is null ? Null : Parse(node.ToJsonString());

    /// <summary>Creates a payload that is a single JSON string literal.</summary>
    public static JsonPayload FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new JsonPayload(JsonValue.Create(value).ToJsonString());
    }

    /// <summary>Parses the payload into a new, independent mutable JSON node.</summary>
    public JsonNode? ToNode() => JsonNode.Parse(Json);

    /// <summary>Parses the payload into a read-only document. The caller disposes it.</summary>
    public JsonDocument ToDocument() => JsonDocument.Parse(Json, ValidationOptions);

    /// <summary>
    /// Returns the string value of a top-level property of a JSON object payload, or
    /// <see langword="null"/> when the payload is not an object, the property is missing,
    /// or the property is not a string.
    /// </summary>
    public string? GetString(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        using var document = ToDocument();
        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    public bool Equals(JsonPayload? other) =>
        other is not null && string.Equals(Json, other.Json, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is JsonPayload other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Json);

    public override string ToString() => Json;
}
