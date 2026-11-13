using System.Buffers;
using ModularRuntime.Contracts;
using static ModularRuntime.Engine.Tests.PlatformTestHelpers;

namespace ModularRuntime.Engine.Tests;

public sealed class BinaryServiceTests
{
    private const string ServiceId = "test.bytes";

    private static BinaryServiceDefinition Definition(params string[] operations) =>
        new(ServiceId, "Bytes", new Version(1, 0), operations.Length == 0 ? ["reverse", "echo", "fail", "empty", "pooled"] : operations);

    private static ValueTask<BinaryResponse> Handler(BinaryRequest request, CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case "reverse":
                var reversed = request.Payload.ToArray();
                Array.Reverse(reversed);
                return ValueTask.FromResult(BinaryResponse.Success(reversed));
            case "echo":
                return ValueTask.FromResult(BinaryResponse.Success(request.Payload.ToArray()));
            case "empty":
                return ValueTask.FromResult(BinaryResponse.Empty);
            case "pooled":
                var owner = MemoryPool<byte>.Shared.Rent(request.Payload.Length);
                request.Payload.CopyTo(owner.Memory);
                return ValueTask.FromResult(BinaryResponse.Success(owner, request.Payload.Length));
            default:
                return ValueTask.FromResult(BinaryResponse.Failure("bad_request", "unsupported"));
        }
    }

    [Fact]
    public async Task Register_Discover_AndInvoke_WithoutCopyingTheRequest()
    {
        await using var engine = new PluginEngine();
        ReadOnlyMemory<byte> seen = default;
        await StartPluginAsync(engine, "com.test.provider", c =>
        {
            c.Services.RegisterBinary(Definition() with { MaxRequestBytes = 1024, MaxResponseBytes = 512 }, (request, ct) =>
            {
                seen = request.Payload;
                return Handler(request, ct);
            });
            return Task.CompletedTask;
        });

        var descriptor = Assert.Single(engine.Host.Services.GetServices());
        Assert.Equal(ServiceKind.Binary, descriptor.Kind);
        Assert.Equal(1024, descriptor.MaxRequestBytes);
        Assert.Equal(512, descriptor.MaxResponseBytes);
        Assert.Null(descriptor.ContractTypeName);

        var request = new byte[] { 1, 2, 3 };
        using var result = await engine.Host.Services.InvokeBinaryAsync(ServiceId, "reverse", request);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal([3, 2, 1], result.Payload.ToArray());
        Assert.Equal(PluginId.Parse("com.test.provider"), result.Provider!.PluginId);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(seen, out var segment) && ReferenceEquals(segment.Array, request), "the request bytes are borrowed, not copied");
    }

    [Fact]
    public async Task ResponseOwner_IsTransferredToTheCaller_AndDisposedWithTheResult()
    {
        await using var engine = new PluginEngine();
        engine.Host.Services.RegisterBinary(Definition(), Handler);

        var result = await engine.Host.Services.InvokeBinaryAsync(ServiceId, "pooled", new byte[] { 9, 8, 7 });
        Assert.Equal([9, 8, 7], result.Payload.ToArray());
        var copy = result.ToArray();
        result.Dispose();
        result.Dispose();

        Assert.True(result.Payload.IsEmpty);
        Assert.Equal([9, 8, 7], copy);
    }

    [Fact]
    public async Task Limits_AreEnforced_PerService_AndNeverAboveTheRuntimeLimit()
    {
        await using var engine = new PluginEngine(limits: new RuntimeLimits { MaxBinaryPayloadBytes = 64 });
        var disposed = false;
        engine.Host.Services.RegisterBinary(Definition("big", "echo") with { MaxRequestBytes = 8, MaxResponseBytes = 1_000_000 }, (request, _) =>
            ValueTask.FromResult(request.Operation == "big"
                ? BinaryResponse.Success(new DisposeTracker(() => disposed = true, 100), 100)
                : BinaryResponse.Success(request.Payload.ToArray())));

        var descriptor = engine.Host.Services.GetService(ServiceId)!;
        Assert.Equal(8, descriptor.MaxRequestBytes);
        Assert.Equal(64, descriptor.MaxResponseBytes);

        using var tooLarge = await engine.Host.Services.InvokeBinaryAsync(ServiceId, "echo", new byte[9]);
        Assert.Equal(PluginOperationStatus.Rejected, tooLarge.Status);
        Assert.Equal(PluginErrorCodes.PayloadTooLarge, tooLarge.ErrorCode);

        using var ok = await engine.Host.Services.InvokeBinaryAsync(ServiceId, "echo", new byte[8]);
        Assert.True(ok.IsSuccess);

        using var bigResponse = await engine.Host.Services.InvokeBinaryAsync(ServiceId, "big");
        Assert.Equal(PluginOperationStatus.Failed, bigResponse.Status);
        Assert.Equal(PluginErrorCodes.PayloadTooLarge, bigResponse.ErrorCode);
        Assert.True(disposed, "an oversized response's buffer is returned to its owner");

        Assert.Throws<ArgumentException>(() => engine.Host.Services.RegisterBinary(new BinaryServiceDefinition("test.other", "x", new Version(1, 0), ["a"]) { MaxRequestBytes = 0 }, Handler));
    }

    [Fact]
    public async Task Failures_Rejections_AndCancellation_FollowTheJsonRules()
    {
        await using var engine = new PluginEngine();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Host.Services.RegisterBinary(Definition("fail", "throw", "wait", "null"), async (request, ct) =>
        {
            switch (request.Operation)
            {
                case "fail":
                    return BinaryResponse.Failure("bad_request", "nope");
                case "throw":
                    throw new InvalidOperationException("boom");
                case "null":
                    return null!;
                default:
                    entered.TrySetResult();
                    await Task.Delay(System.Threading.Timeout.Infinite, ct);
                    return BinaryResponse.Empty;
            }
        });

        using var failed = await engine.Host.Services.InvokeBinaryAsync(ServiceId, "fail");
        Assert.Equal(PluginOperationStatus.Failed, failed.Status);
        Assert.Equal("bad_request", failed.ErrorCode);

        using var thrown = await engine.Host.Services.InvokeBinaryAsync(ServiceId, "throw");
        Assert.Equal(PluginErrorCodes.HandlerFailed, thrown.ErrorCode);
        Assert.Equal(typeof(InvalidOperationException).FullName, thrown.Diagnostic!.ExceptionType);

        using var nullResponse = await engine.Host.Services.InvokeBinaryAsync(ServiceId, "null");
        Assert.Equal(PluginErrorCodes.InvalidResponse, nullResponse.ErrorCode);

        using var unsupported = await engine.Host.Services.InvokeBinaryAsync(ServiceId, "other");
        Assert.Equal(PluginErrorCodes.OperationNotSupported, unsupported.ErrorCode);

        using var missing = await engine.Host.Services.InvokeBinaryAsync("test.missing", "wait");
        Assert.Equal(PluginErrorCodes.ServiceNotFound, missing.ErrorCode);

        var jsonOnBinary = await engine.Host.Services.InvokeAsync(ServiceId, "fail");
        Assert.Equal(PluginErrorCodes.ServiceKindMismatch, jsonOnBinary.ErrorCode);

        using var cancellation = new CancellationTokenSource();
        var call = engine.Host.Services.InvokeBinaryAsync(ServiceId, "wait", cancellationToken: cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        using var canceled = await call.WaitAsync(TestTimeout);
        Assert.Equal(PluginOperationStatus.Canceled, canceled.Status);
    }

    [Fact]
    public async Task Client_IsBoundToTheRegistration_AndSurvivesTheProviderOnlyAsAnInactiveHandle()
    {
        await using var engine = new PluginEngine();
        await StartPluginAsync(engine, "com.test.provider", c =>
        {
            c.Services.RegisterBinary(Definition(), Handler);
            return Task.CompletedTask;
        });
        var client = engine.Host.Services.GetClient(ServiceId)!;
        using (var echoed = await client.InvokeBinaryAsync("echo", new byte[] { 4 }))
        {
            Assert.Equal([4], echoed.Payload.ToArray());
        }

        Assert.True((await engine.StopAsync(PluginId.Parse("com.test.provider"))).IsSuccess);

        Assert.False(client.IsAvailable);
        using var removed = await client.InvokeBinaryAsync("echo");
        Assert.Equal(PluginErrorCodes.ServiceRemoved, removed.ErrorCode);
        Assert.Empty(engine.Host.Services.GetServices());
    }

    private sealed class DisposeTracker(Action onDispose, int size) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; } = new byte[size];

        public void Dispose() => onDispose();
    }
}
