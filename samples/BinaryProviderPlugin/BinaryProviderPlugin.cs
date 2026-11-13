using System.Buffers;
using System.Buffers.Binary;
using ModularRuntime.Contracts;

namespace BinaryProviderPlugin;

/// <summary>
/// Provides <c>example.checksum</c> 1.0, a binary service with <c>crc32</c> (4-byte big-endian
/// CRC-32 of the request) and <c>reverse</c> (the request bytes reversed, in a pooled buffer).
/// </summary>
/// <remarks>
/// Ownership rules: the request bytes are borrowed until the handler completes; the response bytes
/// belong to the caller, which disposes the result. <c>reverse</c> shows a pooled response: the
/// buffer returns to the pool when the caller disposes the result.
/// </remarks>
public sealed class BinaryProviderPlugin : IPlugin
{
    public const string ServiceId = "example.checksum";

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Services.RegisterBinary(
            new BinaryServiceDefinition(ServiceId, "Checksums", new Version(1, 0), ["crc32", "reverse"])
            {
                MaxRequestBytes = 1024 * 1024,
                MaxResponseBytes = 1024 * 1024,
                Metadata = new Dictionary<string, string> { ["description"] = "CRC-32 and reversal of request bytes." },
            },
            HandleAsync);
        context.Logger.LogInformation($"BinaryProviderPlugin registered binary service '{ServiceId}' 1.0.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static ValueTask<BinaryResponse> HandleAsync(BinaryRequest request, CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case "crc32":
                var crc = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(request.Payload.Span));
                return ValueTask.FromResult(BinaryResponse.Success(crc));

            case "reverse":
                var owner = MemoryPool<byte>.Shared.Rent(request.Payload.Length);
                request.Payload.Span.CopyTo(owner.Memory.Span);
                owner.Memory.Span[..request.Payload.Length].Reverse();
                return ValueTask.FromResult(BinaryResponse.Success(owner, request.Payload.Length));

            default:
                return ValueTask.FromResult(BinaryResponse.Failure("operation_not_supported", $"Unknown operation '{request.Operation}'."));
        }
    }

    /// <summary>CRC-32 (IEEE 802.3), computed inline to keep the sample free of package dependencies.</summary>
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var entry = index;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? 0xEDB88320u ^ (entry >> 1) : entry >> 1;
            }

            table[index] = entry;
        }

        return table;
    }
}
