using ModularRuntime.Contracts;
using static ModularRuntime.Engine.Tests.PlatformTestHelpers;

namespace ModularRuntime.Engine.Tests;

public sealed class ServiceRegistryTests
{
    private const string ServiceId = "test.text";

    private static ValueTask<PluginResponse> Upper(PluginRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.Operation switch
        {
            "upper" => PluginResponse.Success(JsonPayload.FromString((request.Payload.ToNode()?.GetValue<string>() ?? string.Empty).ToUpperInvariant())),
            _ => PluginResponse.Failure("unexpected", "unexpected operation"),
        });

    [Fact]
    public async Task Register_Discover_AndInvoke()
    {
        await using var engine = new PluginEngine();
        await StartPluginAsync(engine, "com.test.provider", c =>
        {
            c.Services.Register(Service(ServiceId, "1.2", "upper") with { Metadata = new Dictionary<string, string> { ["lang"] = "en" } }, Upper);
            return Task.CompletedTask;
        });

        var descriptor = Assert.Single(engine.Host.Services.GetServices());
        Assert.Equal(ServiceId, descriptor.ServiceId);
        Assert.Equal(new Version(1, 2), descriptor.Version);
        Assert.Equal(["upper"], descriptor.Operations);
        Assert.Equal("en", descriptor.Metadata["lang"]);
        Assert.Equal(PluginId.Parse("com.test.provider"), descriptor.Owner.PluginId);
        Assert.True(descriptor.IsAvailable);

        var result = await engine.Host.Services.InvokeAsync(ServiceId, "upper", JsonPayload.FromString("hello"));

        Assert.True(result.IsSuccess);
        Assert.Equal("\"HELLO\"", result.Payload!.Json);
        Assert.Equal(PluginId.Parse("com.test.provider"), result.Provider!.PluginId);
    }

    [Fact]
    public async Task Request_CarriesCallerIdentityAndCorrelation()
    {
        await using var engine = new PluginEngine();
        PluginRequest? seen = null;
        engine.Host.Services.Register(Service(ServiceId), (request, _) => { seen = request; return ValueTask.FromResult(PluginResponse.Success()); });
        IPluginContext? consumer = null;
        await StartPluginAsync(engine, "com.test.consumer", c => { consumer = c; return Task.CompletedTask; });

        var result = await consumer!.Services.InvokeAsync(ServiceId, "echo", Json("{}"), new ServiceInvocationOptions { CorrelationId = "c-7" });

        Assert.True(result.IsSuccess);
        Assert.Equal(consumer.InstanceId, seen!.Caller.InstanceId);
        Assert.Equal("c-7", seen.CorrelationId);
        Assert.True(result.Provider!.IsHost);
    }

    [Fact]
    public async Task DuplicateServiceId_IsRejected()
    {
        await using var engine = new PluginEngine();
        engine.Host.Services.Register(Service(ServiceId), Echo);
        PluginRuntimeException? error = null;
        await StartPluginAsync(engine, "com.test.duplicate", c =>
        {
            error = Assert.Throws<PluginRuntimeException>(() => c.Services.Register(Service(ServiceId), Echo));
            return Task.CompletedTask;
        });

        Assert.Equal(PluginErrorCodes.DuplicateService, error!.ErrorCode);
        Assert.True(Assert.Single(engine.Host.Services.GetServices()).Owner.IsHost);
    }

    [Fact]
    public async Task InvalidDefinitions_AreRejected()
    {
        await using var engine = new PluginEngine();
        var services = engine.Host.Services;

        Assert.Throws<ArgumentException>(() => services.Register(Service("Bad Id"), Echo));
        Assert.Throws<ArgumentException>(() => services.Register(new PluginServiceDefinition(ServiceId, "x", new Version(1, 0), []), Echo));
        Assert.Throws<ArgumentException>(() => services.Register(new PluginServiceDefinition(ServiceId, "x", new Version(1, 0), ["a", "a"]), Echo));
        Assert.Throws<ArgumentException>(() => services.Register(new PluginServiceDefinition(ServiceId, " ", new Version(1, 0), ["a"]), Echo));
        Assert.Throws<ArgumentNullException>(() => services.Register(Service(ServiceId), null!));
        Assert.Empty(services.GetServices());
    }

    [Fact]
    public async Task UnknownService_ReturnsNotFound()
    {
        await using var engine = new PluginEngine();

        var result = await engine.Host.Services.InvokeAsync("test.missing", "echo");

        Assert.Equal(PluginOperationStatus.Rejected, result.Status);
        Assert.Equal(PluginErrorCodes.ServiceNotFound, result.ErrorCode);
        Assert.Null(engine.Host.Services.GetClient("test.missing"));
        Assert.Null(engine.Host.Services.GetService("test.missing"));
    }

    [Fact]
    public async Task RemovedService_IsReportedAsNotFoundByIdAndRemovedThroughClient()
    {
        await using var engine = new PluginEngine();
        var registration = engine.Host.Services.Register(Service(ServiceId), Echo);
        var client = engine.Host.Services.GetClient(ServiceId)!;
        Assert.True(client.IsAvailable);

        await registration.DisposeAsync();
        await registration.DisposeAsync();

        Assert.False(registration.IsActive);
        Assert.False(client.IsAvailable);
        Assert.Null(client.GetDescriptor());
        Assert.Equal(PluginErrorCodes.ServiceRemoved, (await client.InvokeAsync("echo")).ErrorCode);
        Assert.Equal(PluginErrorCodes.ServiceNotFound, (await engine.Host.Services.InvokeAsync(ServiceId, "echo")).ErrorCode);
    }

    [Fact]
    public async Task IncompatibleVersion_IsRejected()
    {
        await using var engine = new PluginEngine();
        engine.Host.Services.Register(Service(ServiceId, "1.2"), Echo);

        var tooNew = await engine.Host.Services.InvokeAsync(ServiceId, "echo", options: new ServiceInvocationOptions { MinimumVersion = new Version(1, 3) });
        var otherMajor = await engine.Host.Services.InvokeAsync(ServiceId, "echo", options: new ServiceInvocationOptions { MinimumVersion = new Version(2, 0) });
        var compatible = await engine.Host.Services.InvokeAsync(ServiceId, "echo", options: new ServiceInvocationOptions { MinimumVersion = new Version(1, 2, 0) });

        Assert.Equal(PluginErrorCodes.ServiceVersionIncompatible, tooNew.ErrorCode);
        Assert.Equal(PluginErrorCodes.ServiceVersionIncompatible, otherMajor.ErrorCode);
        Assert.True(compatible.IsSuccess);
        Assert.Null(engine.Host.Services.GetClient(ServiceId, new Version(2, 0)));
        Assert.NotNull(engine.Host.Services.GetClient(ServiceId, new Version(1, 0)));
    }

    [Fact]
    public async Task UnsupportedOperation_IsRejectedWithoutCallingTheHandler()
    {
        await using var engine = new PluginEngine();
        var calls = 0;
        engine.Host.Services.Register(Service(ServiceId, "1.0", "upper"), (r, ct) => { calls++; return Upper(r, ct); });

        var result = await engine.Host.Services.InvokeAsync(ServiceId, "delete", Json("{}"));
        var invalidName = await engine.Host.Services.InvokeAsync(ServiceId, "Not Valid", Json("{}"));

        Assert.Equal(PluginErrorCodes.OperationNotSupported, result.ErrorCode);
        Assert.Equal(PluginErrorCodes.InvalidIdentifier, invalidName.ErrorCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OversizedRequestAndResponse_AreRejected()
    {
        await using var engine = new PluginEngine(limits: new RuntimeLimits { MaxPayloadBytes = 32 });
        engine.Host.Services.Register(Service(ServiceId, "1.0", "echo", "inflate"), (request, _) =>
            ValueTask.FromResult(request.Operation == "inflate"
                ? PluginResponse.Success(JsonPayload.FromString(new string('x', 64)))
                : PluginResponse.Success(request.Payload)));

        var request = await engine.Host.Services.InvokeAsync(ServiceId, "echo", JsonPayload.FromString(new string('y', 64)));
        var response = await engine.Host.Services.InvokeAsync(ServiceId, "inflate");

        Assert.Equal(PluginOperationStatus.Rejected, request.Status);
        Assert.Equal(PluginErrorCodes.PayloadTooLarge, request.ErrorCode);
        Assert.Equal(PluginOperationStatus.Failed, response.Status);
        Assert.Equal(PluginErrorCodes.PayloadTooLarge, response.ErrorCode);
    }

    [Fact]
    public async Task ThrowingHandler_IsReportedAsHandlerFailedWithTextDiagnostic()
    {
        await using var engine = new PluginEngine();
        engine.Host.Services.Register(Service(ServiceId), (_, _) => throw new FormatException("bad input"));

        var result = await engine.Host.Services.InvokeAsync(ServiceId, "echo");

        Assert.Equal(PluginOperationStatus.Failed, result.Status);
        Assert.Equal(PluginErrorCodes.HandlerFailed, result.ErrorCode);
        Assert.Equal("System.FormatException", result.Diagnostic!.ExceptionType);
        Assert.True(engine.Host.Services.GetService(ServiceId)!.IsAvailable);
    }

    [Fact]
    public async Task HandlerReportedFailure_AndNullResponse_AreFailures()
    {
        await using var engine = new PluginEngine();
        engine.Host.Services.Register(Service(ServiceId, "1.0", "fail", "null"), (request, _) =>
            ValueTask.FromResult(request.Operation == "fail" ? PluginResponse.Failure("invalid_payload", "missing text") : null!));

        var failure = await engine.Host.Services.InvokeAsync(ServiceId, "fail");
        var nullResponse = await engine.Host.Services.InvokeAsync(ServiceId, "null");

        Assert.Equal(PluginOperationStatus.Failed, failure.Status);
        Assert.Equal("invalid_payload", failure.ErrorCode);
        Assert.Equal("missing text", failure.Message);
        Assert.Equal(PluginErrorCodes.InvalidResponse, nullResponse.ErrorCode);
    }

    [Fact]
    public async Task CallerCancellation_CancelsTheHandler()
    {
        await using var engine = new PluginEngine();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Host.Services.Register(Service(ServiceId), async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return PluginResponse.Success();
        });
        using var cancellation = new CancellationTokenSource();

        var invocation = engine.Host.Services.InvokeAsync(ServiceId, "echo", cancellationToken: cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TestTimeout);
        await cancellation.CancelAsync();
        var result = await invocation.WaitAsync(TestTimeout);

        Assert.Equal(PluginOperationStatus.Canceled, result.Status);
        Assert.Equal(PluginErrorCodes.OperationCanceled, result.ErrorCode);
    }

    [Fact]
    public async Task ConcurrentInvocations_AllSucceed()
    {
        await using var engine = new PluginEngine();
        var active = 0;
        var maxActive = 0;
        await StartPluginAsync(engine, "com.test.concurrent-provider", c =>
        {
            c.Services.Register(Service(ServiceId), async (request, _) =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedMax(ref maxActive, now);
                await Task.Yield();
                Interlocked.Decrement(ref active);
                return PluginResponse.Success(request.Payload);
            });
            return Task.CompletedTask;
        });
        var client = engine.Host.Services.GetClient(ServiceId)!;

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            Task.Run(async () => await client.InvokeAsync("echo", Json(i.ToString(System.Globalization.CultureInfo.InvariantCulture)))))).WaitAsync(TestTimeout);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(Enumerable.Range(0, 200).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), results.Select(r => r.Payload!.Json));
        Assert.True(maxActive >= 1);
    }

    [Fact]
    public async Task ProviderStoppedDuringInvocation_CancelsDrainsAndRemovesTheService()
    {
        await using var engine = new PluginEngine();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCompleted = false;
        var shutdownAfterHandler = false;
        await StartPluginAsync(
            engine,
            "com.test.stopping-provider",
            c =>
            {
                c.Services.Register(Service(ServiceId), async (_, ct) =>
                {
                    entered.TrySetResult();
                    try
                    {
                        await Task.Delay(System.Threading.Timeout.Infinite, ct);
                        return PluginResponse.Success();
                    }
                    finally
                    {
                        // Asynchronous cleanup after cancellation: only a real drain waits for it.
                        await Task.Delay(50, CancellationToken.None);
                        handlerCompleted = true;
                    }
                });
                return Task.CompletedTask;
            },
            onShutdown: _ =>
            {
                shutdownAfterHandler = handlerCompleted;
                return Task.CompletedTask;
            });
        var client = engine.Host.Services.GetClient(ServiceId)!;

        var invocation = client.InvokeAsync("echo").AsTask();
        await entered.Task.WaitAsync(TestTimeout);
        var stop = await engine.StopAsync(PluginId.Parse("com.test.stopping-provider")).WaitAsync(TestTimeout);
        var result = await invocation.WaitAsync(TestTimeout);

        Assert.True(stop.IsSuccess);
        Assert.Equal(PluginOperationStatus.Canceled, result.Status);
        Assert.True(shutdownAfterHandler);
        Assert.Equal(PluginErrorCodes.ServiceRemoved, (await client.InvokeAsync("echo")).ErrorCode);
        Assert.Empty(engine.Host.Services.GetServices());
    }

    [Fact]
    public async Task ServiceIsUnavailableWhileItsProviderShutsDown()
    {
        await using var engine = new PluginEngine();
        PluginInvocationResult? duringShutdown = null;
        PluginServiceDescriptor? descriptorDuringShutdown = null;
        await StartPluginAsync(
            engine,
            "com.test.unavailable",
            c => { c.Services.Register(Service(ServiceId), Echo); return Task.CompletedTask; },
            onShutdown: async _ =>
            {
                descriptorDuringShutdown = engine.Host.Services.GetService(ServiceId);
                duringShutdown = await engine.Host.Services.InvokeAsync(ServiceId, "echo", cancellationToken: CancellationToken.None);
            });

        await engine.StopAsync(PluginId.Parse("com.test.unavailable"));

        Assert.False(descriptorDuringShutdown!.IsAvailable);
        Assert.Equal(PluginErrorCodes.ServiceUnavailable, duringShutdown!.ErrorCode);
    }

    [Fact]
    public async Task RetainedClient_StaysInactiveEvenIfTheServiceIdIsRegisteredAgain()
    {
        await using var engine = new PluginEngine();
        await StartPluginAsync(engine, "com.test.old-provider", c => { c.Services.Register(Service(ServiceId), Echo); return Task.CompletedTask; });
        var oldClient = engine.Host.Services.GetClient(ServiceId)!;
        await engine.StopAsync(PluginId.Parse("com.test.old-provider"));

        var newRegistration = engine.Host.Services.Register(Service(ServiceId), Echo);

        Assert.False(oldClient.IsAvailable);
        Assert.Equal(PluginErrorCodes.ServiceRemoved, (await oldClient.InvokeAsync("echo")).ErrorCode);
        Assert.NotEqual(oldClient.RegistrationId, newRegistration.Descriptor.RegistrationId);
        Assert.True((await engine.Host.Services.GetClient(ServiceId)!.InvokeAsync("echo")).IsSuccess);
    }

    [Fact]
    public async Task Services_AreRemovedAutomaticallyOnStopAndOnFailedStart()
    {
        await using var engine = new PluginEngine();
        await StartPluginAsync(engine, "com.test.auto-service", c => { c.Services.Register(Service("test.a"), Echo); return Task.CompletedTask; });
        var (_, failed) = await TryStartPluginAsync(engine, "com.test.failing-service", c =>
        {
            c.Services.Register(Service("test.b"), Echo);
            throw new InvalidOperationException("fails after registering");
        });

        Assert.Equal(PluginErrorCodes.InitializationFailed, failed.ErrorCode);
        Assert.Equal(["test.a"], engine.Host.Services.GetServices().Select(s => s.ServiceId));

        await engine.StopAsync(PluginId.Parse("com.test.auto-service"));
        Assert.Empty(engine.Host.Services.GetServices());
    }

    [Fact]
    public async Task TwoRuntimes_DoNotShareServices()
    {
        await using var first = new PluginEngine();
        await using var second = new PluginEngine();
        first.Host.Services.Register(Service(ServiceId), Echo);

        Assert.Empty(second.Host.Services.GetServices());
        Assert.Equal(PluginErrorCodes.ServiceNotFound, (await second.Host.Services.InvokeAsync(ServiceId, "echo")).ErrorCode);
        second.Host.Services.Register(Service(ServiceId), Echo);
        Assert.Single(first.Host.Services.GetServices());
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
