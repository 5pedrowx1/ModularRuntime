using System.Text.Json;
using ModularRuntime.Packaging;

namespace ModularRuntime.Registry;

/// <summary>Fetches bytes for the registry client (abstracted for tests and offline sources).</summary>
public interface IRegistryTransport
{
    /// <summary>Downloads at most <paramref name="maxBytes"/> bytes into <paramref name="destination"/>.</summary>
    Task DownloadAsync(Uri uri, Stream destination, long maxBytes, IProgress<long>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// <c>https</c> through <see cref="HttpClient"/>, and <c>file</c> URIs (a registry published to a
/// local or network directory, used for offline mirrors and tests). Other schemes are refused.
/// </summary>
public sealed class DefaultRegistryTransport(HttpClient httpClient) : IRegistryTransport
{
    public async Task DownloadAsync(Uri uri, Stream destination, long maxBytes, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(destination);
        Stream source;
        HttpResponseMessage? response = null;
        try
        {
            if (uri.IsFile)
            {
                source = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            }
            else if (uri.Scheme == Uri.UriSchemeHttps)
            {
                response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new RegistryException(RegistryErrorCodes.DownloadFailed, $"'{uri}' returned HTTP {(int)response.StatusCode}.");
                }

                if (response.Content.Headers.ContentLength > maxBytes)
                {
                    throw new RegistryException(RegistryErrorCodes.DownloadTooLarge, $"'{uri}' is larger than {maxBytes} bytes.");
                }

                source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new RegistryException(RegistryErrorCodes.DownloadFailed, $"Scheme '{uri.Scheme}' is not allowed; use https.");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            response?.Dispose();
            throw new RegistryException(RegistryErrorCodes.DownloadFailed, $"'{uri}' could not be downloaded: {exception.Message}", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw new RegistryException(RegistryErrorCodes.DownloadFailed, $"'{uri}' timed out.", exception);
        }

        try
        {
            await using (source.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new RegistryException(RegistryErrorCodes.DownloadTooLarge, $"'{uri}' is larger than {maxBytes} bytes.");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(total);
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new RegistryException(RegistryErrorCodes.DownloadFailed, $"The download of '{uri}' was interrupted: {exception.Message}", exception);
        }
        finally
        {
            response?.Dispose();
        }
    }
}

/// <summary>A verified registry document and where it came from.</summary>
public sealed record RegistrySnapshot(RegistryDocument Document, string Sha256, DateTimeOffset VerifiedAt, string SigningKeyId, bool FromCache, Uri Location);

/// <summary>Registry client configuration.</summary>
public sealed record RegistryOptions
{
    /// <summary>
    /// Base location of the registry (a directory URL ending with <c>/</c> that contains
    /// <c>registry.json</c> and <c>registry.sig</c>): <c>https</c>, or <c>file</c> for a mirror.
    /// <see langword="null"/> when the product has no official registry configured.
    /// </summary>
    public Uri? Location { get; init; }

    public long MaxRegistryBytes { get; init; } = 8 * 1024 * 1024;

    public int MaxSignatureBytes { get; init; } = 16 * 1024;

    /// <summary>Tolerated clock difference for <c>generatedAt</c> in the future.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Downloads, authenticates, and caches the official registry.
/// </summary>
/// <remarks>
/// <para>
/// A catalog is used only after <c>registry.sig</c> verifies over the exact <c>registry.json</c>
/// bytes with a key trusted for registry signing. Then its schema is validated, then freshness:
/// <c>expiresAt</c> must not have passed, and <c>revision</c> must not be lower than the highest
/// revision this profile has accepted (stored in <c>state.json</c>); the same revision with
/// different content is rejected as a conflict.
/// </para>
/// <para>
/// Replay limits: a profile that never saw a newer revision (a new installation) or that stays
/// offline accepts any unexpired, validly signed catalog; an attacker who can serve content can then
/// hold back updates until the catalog expires, but cannot forge entries or packages.
/// </para>
/// </remarks>
public sealed class RegistryClient : IDisposable
{
    private const string RegistryFile = "registry.json";
    private const string SignatureFile = "registry.sig";
    private const string StateFile = "state.json";

    private readonly RegistryOptions _options;
    private readonly TrustStore _trustStore;
    private readonly IRegistryTransport _transport;
    private readonly string _stateDirectory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public RegistryClient(RegistryOptions options, TrustStore trustStore, IRegistryTransport transport, string stateDirectory, TimeProvider? time = null)
    {
        _options = options;
        _trustStore = trustStore;
        _transport = transport;
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _time = time ?? TimeProvider.System;
    }

    public Uri? Location => _options.Location;

    public void Dispose() => _refreshLock.Dispose();

    public bool IsConfigured => _options.Location is not null;

    /// <summary>Highest revision accepted by this profile so far (0 when none).</summary>
    public long HighestAcceptedRevision => ReadState().HighestRevision;

    /// <summary>Downloads and verifies the current catalog, then caches it for offline use.</summary>
    public async Task<RegistrySnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var location = _options.Location ?? throw new RegistryException(RegistryErrorCodes.NotConfigured, "No official registry location is configured.");
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] registryBytes;
            byte[] signatureBytes;
            try
            {
                registryBytes = await DownloadBytesAsync(new Uri(location, RegistryFile), _options.MaxRegistryBytes, cancellationToken).ConfigureAwait(false);
                signatureBytes = await DownloadBytesAsync(new Uri(location, SignatureFile), _options.MaxSignatureBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (RegistryException exception) when (exception.Code is RegistryErrorCodes.DownloadFailed)
            {
                throw new RegistryException(RegistryErrorCodes.Unavailable, $"The registry is unavailable: {exception.Message}", exception);
            }

            var snapshot = Authenticate(registryBytes, signatureBytes, fromCache: false, location);
            var state = ReadState();
            Directory.CreateDirectory(_stateDirectory);
            WriteAtomic(Path.Combine(_stateDirectory, RegistryFile), registryBytes);
            WriteAtomic(Path.Combine(_stateDirectory, SignatureFile), signatureBytes);
            if (snapshot.Document.Revision > state.HighestRevision)
            {
                WriteAtomic(Path.Combine(_stateDirectory, StateFile), JsonSerializer.SerializeToUtf8Bytes(
                    new RegistryState(snapshot.Document.Revision, snapshot.Sha256, _time.GetUtcNow())));
            }

            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>The last catalog accepted by this profile, re-verified now; <see langword="null"/> when none is cached.</summary>
    public RegistrySnapshot? LoadCached()
    {
        var registryPath = Path.Combine(_stateDirectory, RegistryFile);
        var signaturePath = Path.Combine(_stateDirectory, SignatureFile);
        if (!File.Exists(registryPath) || !File.Exists(signaturePath) || _options.Location is null)
        {
            return null;
        }

        return Authenticate(File.ReadAllBytes(registryPath), File.ReadAllBytes(signaturePath), fromCache: true, _options.Location);
    }

    /// <summary>
    /// The last catalog accepted by this profile for <em>revocation checks only</em>: its signature
    /// and schema are verified again, but its expiry is not enforced, because applying an authentic
    /// revocation is always the safe direction. Never use it to install or update anything.
    /// <see langword="null"/> when nothing was cached or the cache does not verify.
    /// </summary>
    public RegistryDocument? LoadCachedRevocationSource()
    {
        var registryPath = Path.Combine(_stateDirectory, RegistryFile);
        var signaturePath = Path.Combine(_stateDirectory, SignatureFile);
        if (!File.Exists(registryPath) || !File.Exists(signaturePath))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(registryPath);
            _trustStore.Verify(bytes, SignatureEnvelope.Parse(File.ReadAllBytes(signaturePath), _options.MaxSignatureBytes), KeyPurposes.Registry);
            return RegistryDocument.Parse(bytes);
        }
        catch (Exception exception) when (exception is PackageException or RegistryException or IOException)
        {
            return null;
        }
    }

    /// <summary>Resolves an entry's download URL (relative URLs against the registry location).</summary>
    public Uri ResolveDownloadUrl(RegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var location = _options.Location ?? throw new RegistryException(RegistryErrorCodes.NotConfigured, "No official registry location is configured.");
        var uri = Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var absolute) && !entry.DownloadUrl.StartsWith('/')
            ? absolute
            : new Uri(location, entry.DownloadUrl);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsFile && location.IsFile))
        {
            throw new RegistryException(RegistryErrorCodes.Invalid, $"The download URL of '{entry.Id}' must use https.");
        }

        return uri;
    }

    /// <summary>Downloads a package to <paramref name="destinationFile"/>, enforcing the size from the signed entry.</summary>
    public async Task DownloadPackageAsync(RegistryEntry entry, string destinationFile, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var uri = ResolveDownloadUrl(entry);
        await using var file = new FileStream(destinationFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await _transport.DownloadAsync(uri, file, entry.PackageSize, progress, cancellationToken).ConfigureAwait(false);
        if (file.Length != entry.PackageSize)
        {
            throw new RegistryException(RegistryErrorCodes.DownloadFailed, $"The package of '{entry.Id}' has {file.Length} bytes; the registry says {entry.PackageSize}.");
        }
    }

    private RegistrySnapshot Authenticate(byte[] registryBytes, byte[] signatureBytes, bool fromCache, Uri location)
    {
        TrustedKey key;
        try
        {
            var envelope = SignatureEnvelope.Parse(signatureBytes, _options.MaxSignatureBytes);
            key = _trustStore.Verify(registryBytes, envelope, KeyPurposes.Registry);
        }
        catch (PackageException exception)
        {
            throw new RegistryException(RegistryErrorCodes.SignatureInvalid, $"The registry signature is not valid: {exception.Message}", exception);
        }

        var document = RegistryDocument.Parse(registryBytes);
        var now = _time.GetUtcNow();
        if (document.GeneratedAt > now + _options.ClockSkew)
        {
            throw new RegistryException(RegistryErrorCodes.NotYetValid, $"The registry was generated in the future ({document.GeneratedAt:O}); check the system clock.");
        }

        if (document.ExpiresAt <= now)
        {
            throw new RegistryException(RegistryErrorCodes.Expired, $"The registry expired at {document.ExpiresAt:O}.");
        }

        var sha256 = Sha256Hex.Compute(registryBytes);
        var state = ReadState();
        if (document.Revision < state.HighestRevision)
        {
            throw new RegistryException(RegistryErrorCodes.Rollback, $"The registry revision {document.Revision} is older than revision {state.HighestRevision}, already accepted by this profile.");
        }

        if (document.Revision == state.HighestRevision && state.Sha256 is { } knownHash && !Sha256Hex.Equal(knownHash, sha256))
        {
            throw new RegistryException(RegistryErrorCodes.Conflict, $"Registry revision {document.Revision} has different content than the revision {state.HighestRevision} already accepted.");
        }

        return new RegistrySnapshot(document, sha256, now, key.KeyId, fromCache, location);
    }

    private async Task<byte[]> DownloadBytesAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await _transport.DownloadAsync(uri, buffer, maxBytes, null, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private RegistryState ReadState()
    {
        var path = Path.Combine(_stateDirectory, StateFile);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<RegistryState>(File.ReadAllBytes(path)) ?? RegistryState.Empty : RegistryState.Empty;
        }
        catch (JsonException)
        {
            // A corrupted state must not silently lower the rollback floor.
            throw new RegistryException(RegistryErrorCodes.Invalid, $"The registry state file '{path}' is corrupted; delete it only if you trust the next catalog.");
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private sealed record RegistryState(long HighestRevision, string? Sha256, DateTimeOffset? AcceptedAt)
    {
        public static RegistryState Empty { get; } = new(0, null, null);
    }
}
