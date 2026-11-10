using System.Text.Json.Nodes;
using ModularRuntime.Contracts;

namespace ServiceConsumerPlugin;

/// <summary>
/// Discovers <c>example.text.transform</c> through the registry and invokes it. The consumer
/// never references the provider's assembly; it only knows the service ID and the JSON shape.
/// </summary>
public sealed class ServiceConsumerPlugin : IPlugin
{
    public const string ServiceId = "example.text.transform";

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var client = context.Services.GetClient(ServiceId, new Version(1, 0));
        if (client is null)
        {
            context.Logger.LogWarning($"ServiceConsumerPlugin: service '{ServiceId}' 1.x is not available.");
            return;
        }

        var descriptor = client.GetDescriptor();
        context.Logger.LogInformation(
            $"ServiceConsumerPlugin found '{ServiceId}' {descriptor?.Version} provided by {descriptor?.Owner}.");

        var request = JsonPayload.FromNode(new JsonObject { ["text"] = "hello from the service consumer" });
        var result = await client.InvokeAsync("uppercase", request, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            context.Logger.LogInformation($"ServiceConsumerPlugin received: {result.Payload!.GetString("text")}");
        }
        else
        {
            context.Logger.LogWarning($"ServiceConsumerPlugin invocation failed: [{result.ErrorCode}] {result.Message}");
        }
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
