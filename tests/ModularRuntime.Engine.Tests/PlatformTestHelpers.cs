using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Tests;

internal static class PlatformTestHelpers
{
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    public static JsonPayload Json(string json) => JsonPayload.Parse(json);

    /// <summary>Registers, validates, and starts a test plugin; asserts success.</summary>
    public static async Task<TestPlugin> StartPluginAsync(
        PluginEngine engine,
        string id,
        Func<IPluginContext, Task>? onInitialize = null,
        Func<CancellationToken, Task>? onShutdown = null,
        int startOrder = 0)
    {
        var (plugin, result) = await TryStartPluginAsync(engine, id, onInitialize, onShutdown, startOrder);
        Assert.True(result.IsSuccess, result.Message);
        return plugin;
    }

    public static async Task<(TestPlugin Plugin, PluginOperationResult Result)> TryStartPluginAsync(
        PluginEngine engine,
        string id,
        Func<IPluginContext, Task>? onInitialize = null,
        Func<CancellationToken, Task>? onShutdown = null,
        int startOrder = 0)
    {
        var descriptor = Create(id, startOrder);
        var plugin = new TestPlugin(
            (context, _) => onInitialize?.Invoke(context) ?? Task.CompletedTask,
            onShutdown);
        Assert.True(engine.Register(descriptor, new TestPluginFactory(plugin)).IsSuccess);
        Assert.True(engine.MarkValidated(descriptor.Id).IsSuccess);
        var result = await engine.StartAsync(descriptor.Id).WaitAsync(TestTimeout);
        return (plugin, result);
    }

    public static PluginDescriptor Create(string id, int startOrder = 0) =>
        new(
            PluginId.Parse(id),
            $"Test Plugin {id}",
            new Version(1, 0, 0),
            PluginApiVersion.Supported,
            "TestPlugin.dll",
            "TestPlugin.TestPlugin",
            startOrder);

    public static PluginServiceDefinition Service(string id, string version = "1.0", params string[] operations) =>
        new(id, $"Service {id}", Version.Parse(version), operations.Length == 0 ? ["echo"] : operations);

    /// <summary>Handler that echoes the request payload.</summary>
    public static ValueTask<PluginResponse> Echo(PluginRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginResponse.Success(request.Payload));

    public static PluginRuntimeException AssertRuntimeError(string expectedCode, Action action)
    {
        var exception = Assert.Throws<PluginRuntimeException>(action);
        Assert.Equal(expectedCode, exception.ErrorCode);
        return exception;
    }
}
