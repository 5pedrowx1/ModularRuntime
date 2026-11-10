using System.Buffers.Binary;
using System.Text;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Ipc.Tests;

public sealed class MessageFramingTests
{
    /// <summary>Stream that returns at most <c>chunk</c> bytes per read, to exercise partial reads.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }

    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    [Fact]
    public async Task RoundTrip_PreservesTheMessage()
    {
        using var stream = new MemoryStream();
        await MessageFraming.WriteAsync(stream, new IpcMessage { Type = IpcMessageTypes.Request, Id = 7, Op = IpcOperations.PluginsList }, 1024, CancellationToken.None);
        stream.Position = 0;

        var message = await MessageFraming.ReadAsync(stream, 1024, CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal(IpcMessageTypes.Request, message.Type);
        Assert.Equal(7, message.Id);
        Assert.Equal(IpcOperations.PluginsList, message.Op);
    }

    [Fact]
    public async Task PartialReads_OneByteAtATime_AreReassembled()
    {
        var bytes = Frame("""{"type":"request","id":1,"op":"plugins.list"}""").Concat(Frame("""{"type":"cancel","id":1}""")).ToArray();
        using var stream = new ChunkedStream(bytes, chunk: 1);

        var first = await MessageFraming.ReadAsync(stream, 1024, CancellationToken.None);
        var second = await MessageFraming.ReadAsync(stream, 1024, CancellationToken.None);
        var end = await MessageFraming.ReadAsync(stream, 1024, CancellationToken.None);

        Assert.Equal("plugins.list", first!.Op);
        Assert.Equal(IpcMessageTypes.Cancel, second!.Type);
        Assert.Null(end);
    }

    [Fact]
    public async Task CleanEndOfStream_ReturnsNull()
    {
        using var stream = new MemoryStream([]);

        Assert.Null(await MessageFraming.ReadAsync(stream, 1024, CancellationToken.None));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public async Task EndOfStreamInsideAFrame_IsAProtocolError(int truncateAt)
    {
        var bytes = Frame("""{"type":"request","id":1}""")[..truncateAt];
        using var stream = new MemoryStream(bytes);

        var error = await Assert.ThrowsAsync<IpcProtocolException>(async () => await MessageFraming.ReadAsync(stream, 1024, CancellationToken.None));

        Assert.Equal(IpcErrorCodes.InvalidMessage, error.Code);
    }

    [Fact]
    public async Task OversizedLength_IsRejectedBeforeAllocating()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
        using var stream = new MemoryStream(header);

        var error = await Assert.ThrowsAsync<IpcProtocolException>(async () => await MessageFraming.ReadAsync(stream, 1024, CancellationToken.None));

        Assert.Equal(IpcErrorCodes.MessageTooLarge, error.Code);
    }

    [Fact]
    public async Task ZeroLength_IsRejected()
    {
        using var stream = new MemoryStream(new byte[4]);

        var error = await Assert.ThrowsAsync<IpcProtocolException>(async () => await MessageFraming.ReadAsync(stream, 1024, CancellationToken.None));

        Assert.Equal(IpcErrorCodes.InvalidMessage, error.Code);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"id":1}""")]
    public async Task InvalidJsonOrMissingType_IsRejected(string json)
    {
        using var stream = new MemoryStream(Frame(json));

        var error = await Assert.ThrowsAsync<IpcProtocolException>(async () => await MessageFraming.ReadAsync(stream, 1024, CancellationToken.None));

        Assert.Equal(IpcErrorCodes.InvalidMessage, error.Code);
    }

    [Fact]
    public async Task WritingAnOversizedMessage_Throws()
    {
        using var stream = new MemoryStream();
        var message = new IpcMessage { Type = IpcMessageTypes.Request, Op = new string('x', 2000) };

        var error = await Assert.ThrowsAsync<IpcProtocolException>(async () => await MessageFraming.WriteAsync(stream, message, 1024, CancellationToken.None));

        Assert.Equal(IpcErrorCodes.MessageTooLarge, error.Code);
        Assert.Equal(0, stream.Length);
    }
}
