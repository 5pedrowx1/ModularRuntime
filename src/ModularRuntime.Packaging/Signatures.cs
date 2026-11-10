using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModularRuntime.Packaging;

/// <summary>What a trusted key may sign.</summary>
[Flags]
public enum KeyPurposes
{
    None = 0,

    /// <summary>Integrity manifests of official plugin packages.</summary>
    Packages = 1,

    /// <summary>The official registry catalog.</summary>
    Registry = 2,

    /// <summary>
    /// Release approval records of the publishing pipeline (reviewer keys). Never trusted by the
    /// daemon: only the Publisher checks them before it signs a package.
    /// </summary>
    Approvals = 4,
}

/// <summary>
/// Detached signature file (<c>integrity.sig</c>, <c>registry.sig</c>).
/// </summary>
/// <remarks>
/// UTF-8 JSON: <c>{"format":"modularruntime-signature/1","algorithm":"ECDSA-P256-SHA256","keyId":"…","signature":"…"}</c>.
/// <c>signature</c> is the base64 of a DER-encoded (RFC 3279 <c>Ecdsa-Sig-Value</c>) ECDSA signature
/// over the SHA-256 of the exact bytes of the signed file. The key ID only selects which trusted
/// key to verify with; a signature never introduces a key.
/// </remarks>
public sealed record SignatureEnvelope(string KeyId, byte[] Signature)
{
    public const string Format = "modularruntime-signature/1";
    public const string Algorithm = "ECDSA-P256-SHA256";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    /// <summary>Signs <paramref name="data"/> with a P-256 private key.</summary>
    public static SignatureEnvelope Sign(ReadOnlySpan<byte> data, ECDsa privateKey, string keyId)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        TrustedKey.ValidateKeyId(keyId);
        if (privateKey.KeySize != 256)
        {
            throw new ArgumentException("Signing keys must be ECDSA P-256.", nameof(privateKey));
        }

        return new SignatureEnvelope(keyId, privateKey.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    public byte[] Serialize()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteString("algorithm", Algorithm);
            writer.WriteString("keyId", KeyId);
            writer.WriteString("signature", Convert.ToBase64String(Signature));
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public static SignatureEnvelope Parse(ReadOnlySpan<byte> bytes, int maxBytes)
    {
        if (bytes.Length > maxBytes)
        {
            throw Invalid("the signature file is too large");
        }

        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(bytes, ReadOptions);
        }
        catch (JsonException exception)
        {
            throw Invalid($"the signature file is not valid JSON ({exception.Message})");
        }

        if (document is null || document.Format != Format || document.Algorithm != Algorithm)
        {
            throw Invalid($"unsupported signature format (expected {Format} with {Algorithm})");
        }

        if (!TrustedKey.IsValidKeyId(document.KeyId))
        {
            throw Invalid("the key ID is missing or invalid");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(document.Signature ?? string.Empty);
        }
        catch (FormatException)
        {
            throw Invalid("the signature is not valid base64");
        }

        if (signature.Length is 0 or > 128)
        {
            throw Invalid("the signature has an invalid length");
        }

        return new SignatureEnvelope(document.KeyId!, signature);
    }

    private static PackageException Invalid(string message) => new(PackageErrorCodes.SignatureInvalid, $"Invalid signature: {message}.");

    private sealed class Document
    {
        [JsonPropertyName("format")]
        public string? Format { get; init; }

        [JsonPropertyName("algorithm")]
        public string? Algorithm { get; init; }

        [JsonPropertyName("keyId")]
        public string? KeyId { get; init; }

        [JsonPropertyName("signature")]
        public string? Signature { get; init; }
    }
}

/// <summary>A public key trusted for some purposes.</summary>
public sealed record TrustedKey(string KeyId, byte[] PublicKeySpki, KeyPurposes Purposes, bool Revoked, bool IsTestKey)
{
    public static bool IsValidKeyId(string? keyId) =>
        keyId is { Length: >= 3 and <= 64 } &&
        keyId.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.') &&
        char.IsAsciiLetterOrDigit(keyId[0]);

    public static void ValidateKeyId(string keyId)
    {
        if (!IsValidKeyId(keyId))
        {
            throw new ArgumentException("Key IDs are 3-64 characters: lowercase letters, digits, '-' and '.'.", nameof(keyId));
        }
    }

    /// <summary>Verifies a P-256 signature with this key.</summary>
    public bool Verify(ReadOnlySpan<byte> data, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(PublicKeySpki, out _);
        return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
}

/// <summary>
/// The set of public keys this installation trusts, by key ID. Built from the official keys compiled
/// into the product and, only when an administrator explicitly passes one to the daemon, a test
/// trust store. Downloaded content can never add keys.
/// </summary>
/// <remarks>
/// Rotation: several keys can be trusted at once; a new key is shipped in a product update before it
/// signs anything, and an old key is marked <c>revoked</c> (signatures by it are rejected) or
/// removed. Revocation of a key that was compromised requires shipping an update that marks it
/// revoked; there is no online revocation check.
/// </remarks>
public sealed class TrustStore
{
    private static readonly JsonSerializerOptions StoreReadOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    private readonly Dictionary<string, TrustedKey> _keys;

    public TrustStore(IEnumerable<TrustedKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!_keys.TryAdd(key.KeyId, key))
            {
                throw new PackageException(PackageErrorCodes.TrustStoreInvalid, $"Key ID '{key.KeyId}' is defined twice.");
            }
        }
    }

    public IReadOnlyCollection<TrustedKey> Keys => _keys.Values;

    /// <summary>True when any trusted (non-revoked) key is a test key.</summary>
    public bool ContainsTestKeys => _keys.Values.Any(k => k.IsTestKey && !k.Revoked);

    /// <summary>The official keys compiled into this build.</summary>
    public static TrustStore LoadOfficial()
    {
        using var stream = typeof(TrustStore).Assembly.GetManifestResourceStream("ModularRuntime.Packaging.OfficialTrustStore.json")
            ?? throw new InvalidOperationException("The official trust store resource is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Parse(memory.ToArray(), allowTestKeys: false);
    }

    /// <summary>Combines stores; a key ID present in both is an error.</summary>
    public TrustStore Merge(TrustStore other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new TrustStore(_keys.Values.Concat(other.Keys));
    }

    /// <summary>
    /// Verifies a detached signature over <paramref name="data"/> and returns the key that made it.
    /// Throws <see cref="PackageException"/> for an unknown, revoked, or unsuitable key, or a bad signature.
    /// </summary>
    public TrustedKey Verify(ReadOnlySpan<byte> data, SignatureEnvelope signature, KeyPurposes purpose)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (!_keys.TryGetValue(signature.KeyId, out var key))
        {
            throw new PackageException(PackageErrorCodes.UnknownSigningKey, $"Signed with key '{signature.KeyId}', which is not a trusted key.");
        }

        if (key.Revoked)
        {
            throw new PackageException(PackageErrorCodes.SigningKeyRevoked, $"Signed with key '{key.KeyId}', which has been revoked.");
        }

        if ((key.Purposes & purpose) == 0)
        {
            throw new PackageException(PackageErrorCodes.UnknownSigningKey, $"Key '{key.KeyId}' is not trusted to sign {purpose.ToString().ToLowerInvariant()}.");
        }

        bool valid;
        try
        {
            valid = key.Verify(data, signature.Signature);
        }
        catch (CryptographicException)
        {
            valid = false;
        }

        if (!valid)
        {
            throw new PackageException(PackageErrorCodes.SignatureInvalid, $"The signature by key '{key.KeyId}' does not match the signed content.");
        }

        return key;
    }

    /// <summary>
    /// Parses a trust store document:
    /// <c>{"schemaVersion":1,"keys":[{"keyId","algorithm":"ECDSA-P256-SHA256","publicKey":"base64 SPKI","purposes":["packages","registry","approvals"],"status":"active|revoked","test":bool}]}</c>.
    /// </summary>
    public static TrustStore Parse(ReadOnlySpan<byte> json, bool allowTestKeys)
    {
        StoreDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<StoreDocument>(json, StoreReadOptions);
        }
        catch (JsonException exception)
        {
            throw new PackageException(PackageErrorCodes.TrustStoreInvalid, $"Invalid trust store: {exception.Message}");
        }

        if (document is not { SchemaVersion: 1, Keys: not null })
        {
            throw new PackageException(PackageErrorCodes.TrustStoreInvalid, "Invalid trust store: schemaVersion 1 and keys are required.");
        }

        var keys = new List<TrustedKey>();
        foreach (var entry in document.Keys)
        {
            if (entry is null || !TrustedKey.IsValidKeyId(entry.KeyId) || entry.Algorithm != SignatureEnvelope.Algorithm)
            {
                throw new PackageException(PackageErrorCodes.TrustStoreInvalid, "Invalid trust store: each key needs a valid keyId and algorithm ECDSA-P256-SHA256.");
            }

            byte[] spki;
            try
            {
                spki = Convert.FromBase64String(entry.PublicKey ?? string.Empty);
                using var probe = ECDsa.Create();
                probe.ImportSubjectPublicKeyInfo(spki, out _);
                if (probe.KeySize != 256)
                {
                    throw new CryptographicException("not a P-256 key");
                }
            }
            catch (Exception exception) when (exception is FormatException or CryptographicException)
            {
                throw new PackageException(PackageErrorCodes.TrustStoreInvalid, $"Invalid trust store: key '{entry.KeyId}' is not a valid P-256 public key.");
            }

            var purposes = KeyPurposes.None;
            foreach (var purpose in entry.Purposes ?? [])
            {
                purposes |= purpose switch
                {
                    "packages" => KeyPurposes.Packages,
                    "registry" => KeyPurposes.Registry,
                    "approvals" => KeyPurposes.Approvals,
                    _ => throw new PackageException(PackageErrorCodes.TrustStoreInvalid, $"Invalid trust store: unknown purpose '{purpose}'."),
                };
            }

            if (purposes == KeyPurposes.None || entry.Status is not ("active" or "revoked"))
            {
                throw new PackageException(PackageErrorCodes.TrustStoreInvalid, $"Invalid trust store: key '{entry.KeyId}' needs purposes and a status (active or revoked).");
            }

            if (entry.Test && !allowTestKeys)
            {
                throw new PackageException(PackageErrorCodes.TrustStoreInvalid, $"Key '{entry.KeyId}' is a test key and cannot be part of the official trust store.");
            }

            keys.Add(new TrustedKey(entry.KeyId!, spki, purposes, entry.Status == "revoked", entry.Test));
        }

        return new TrustStore(keys);
    }

    /// <summary>Serializes a trust store document (used by the publisher for test and release stores).</summary>
    public static byte[] Serialize(IEnumerable<TrustedKey> keys)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartArray("keys");
            foreach (var key in keys.OrderBy(k => k.KeyId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("keyId", key.KeyId);
                writer.WriteString("algorithm", SignatureEnvelope.Algorithm);
                writer.WriteString("publicKey", Convert.ToBase64String(key.PublicKeySpki));
                writer.WriteStartArray("purposes");
                if (key.Purposes.HasFlag(KeyPurposes.Packages))
                {
                    writer.WriteStringValue("packages");
                }

                if (key.Purposes.HasFlag(KeyPurposes.Registry))
                {
                    writer.WriteStringValue("registry");
                }

                if (key.Purposes.HasFlag(KeyPurposes.Approvals))
                {
                    writer.WriteStringValue("approvals");
                }

                writer.WriteEndArray();
                writer.WriteString("status", key.Revoked ? "revoked" : "active");
                writer.WriteBoolean("test", key.IsTestKey);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private sealed class StoreDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int? SchemaVersion { get; init; }

        [JsonPropertyName("keys")]
        public List<KeyDocument?>? Keys { get; init; }
    }

    private sealed class KeyDocument
    {
        [JsonPropertyName("keyId")]
        public string? KeyId { get; init; }

        [JsonPropertyName("algorithm")]
        public string? Algorithm { get; init; }

        [JsonPropertyName("publicKey")]
        public string? PublicKey { get; init; }

        [JsonPropertyName("purposes")]
        public List<string>? Purposes { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("test")]
        public bool Test { get; init; }
    }
}

