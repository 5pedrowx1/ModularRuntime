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
/// Payloads are the only data that crosses plugin boundaries through the JSON mechanisms. They
/// are plain text, so a payload never references a type, delegate, or object from a plugin
/// assembly and can be kept by any party without preventing a plugin's load context from
/// unloading.
/// </para>
/// <para>
/// The canonical representation is UTF-8 (<see cref="Utf8Json"/>); <see cref="Json"/> is
/// produced on first access and cached. Construction validates JSON syntax (maximum depth
/// <see cref="MaxDepth"/>, no comments, no trailing commas) with a forward-only reader and no
/// intermediate document. Size limits are enforced by the runtime when a payload is published or
/// returned (default 256 KiB). The runtime never deserializes payloads into CLR types; consumers
/// parse them explicitly with <see cref="ToNode"/>, <see cref="ToDocument"/>, or a
/// <see cref="Utf8JsonReader"/> over <see cref="Utf8Json"/>.
/// </para>
/// </remarks>
public sealed class JsonPayload : IEquatable<JsonPayload>
{
    /// <summary>Maximum JSON nesting depth accepted by the runtime.</summary>
    public const int MaxDepth = 64;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = MaxDepth,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        MaxDepth = MaxDepth,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    private readonly byte[] _utf8;
    private string? _json;

    private JsonPayload(byte[] utf8, string? json)
    {
        _utf8 = utf8;
        _json = json;
    }

    /// <summary>The JSON literal <c>null</c>.</summary>
    public static JsonPayload Null { get; } = new("null"u8.ToArray(), "null");

    /// <summary>An empty JSON object, <c>{}</c>.</summary>
    public static JsonPayload EmptyObject { get; } = new("{}"u8.ToArray(), "{}");

    /// <summary>The validated JSON text. Decoded from <see cref="Utf8Json"/> on first access and cached.</summary>
    public string Json => _json ?? Volatile.Read(ref _json) ?? Decode();

    /// <summary>The validated JSON as UTF-8 bytes. The memory is immutable.</summary>
    public ReadOnlyMemory<byte> Utf8Json => _utf8;

    /// <summary>Size of the JSON encoded as UTF-8, used for runtime limits.</summary>
    public int ByteCount => _utf8.Length;

    /// <summary>Parses and validates JSON text.</summary>
    /// <exception cref="FormatException">The text is not valid JSON.</exception>
    public static JsonPayload Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return TryParse(json, out var payload, out var error)
            ? payload
            : throw new FormatException(error);
    }

    /// <summary>Parses and validates UTF-8 JSON. The bytes are copied.</summary>
    /// <exception cref="FormatException">The bytes are not valid JSON.</exception>
    public static JsonPayload Parse(ReadOnlySpan<byte> utf8Json) =>
        TryParse(utf8Json, out var payload, out var error)
            ? payload
            : throw new FormatException(error);

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

        var utf8 = Encoding.UTF8.GetBytes(json);
        if (!IsValid(utf8, out error))
        {
            return false;
        }

        payload = new JsonPayload(utf8, json);
        return true;
    }

    /// <summary>Validates UTF-8 JSON without throwing. The bytes are copied into the payload.</summary>
    public static bool TryParse(
        ReadOnlySpan<byte> utf8Json,
        [NotNullWhen(true)] out JsonPayload? payload,
        [NotNullWhen(false)] out string? error)
    {
        payload = null;
        if (!IsValid(utf8Json, out error))
        {
            return false;
        }

        payload = new JsonPayload(utf8Json.ToArray(), null);
        return true;
    }

    /// <summary>Creates a payload from a JSON node (<see langword="null"/> becomes <see cref="Null"/>).</summary>
    public static JsonPayload FromNode(JsonNode? node)
    {
        if (node is null)
        {
            return Null;
        }

        // Nodes always serialize to valid JSON; depth is still enforced by the validation pass.
        return Parse(JsonSerializer.SerializeToUtf8Bytes(node));
    }

    /// <summary>Creates a payload that is a single JSON string literal.</summary>
    public static JsonPayload FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new JsonPayload(JsonSerializer.SerializeToUtf8Bytes(value), null);
    }

    /// <summary>Parses the payload into a new, independent mutable JSON node.</summary>
    public JsonNode? ToNode() => JsonNode.Parse(_utf8, documentOptions: DocumentOptions);

    /// <summary>Parses the payload into a read-only document. The caller disposes it.</summary>
    public JsonDocument ToDocument() => JsonDocument.Parse(_utf8, DocumentOptions);

    /// <summary>Writes the JSON into <paramref name="writer"/> as a raw value, without re-parsing it.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteRawValue(_utf8, skipInputValidation: true);
    }

    /// <summary>
    /// Returns the string value of a top-level property of a JSON object payload, or
    /// <see langword="null"/> when the payload is not an object, the property is missing,
    /// or the property is not a string. Reads forward without building a document.
    /// </summary>
    public string? GetString(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        var reader = new Utf8JsonReader(_utf8, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var matches = reader.ValueTextEquals(propertyName);
            if (!reader.Read())
            {
                return null;
            }

            if (matches)
            {
                return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }

            reader.TrySkip();
        }

        return null;
    }

    public bool Equals(JsonPayload? other) =>
        other is not null && (ReferenceEquals(this, other) || _utf8.AsSpan().SequenceEqual(other._utf8));

    public override bool Equals(object? obj) => obj is JsonPayload other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_utf8);
        return hash.ToHashCode();
    }

    public override string ToString() => Json;

    private string Decode()
    {
        var decoded = Encoding.UTF8.GetString(_utf8);
        return Interlocked.CompareExchange(ref _json, decoded, null) ?? decoded;
    }

    private static bool IsValid(ReadOnlySpan<byte> utf8, [NotNullWhen(false)] out string? error)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, ReaderOptions);
            if (!reader.Read())
            {
                error = "Invalid JSON payload: the text contains no JSON value.";
                return false;
            }

            // Reads the whole value; trailing content after the single value throws.
            while (reader.Read())
            {
            }

            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            error = $"Invalid JSON payload: {exception.Message}";
            return false;
        }
    }
}
