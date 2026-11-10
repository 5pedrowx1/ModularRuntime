using System.Text.Json.Nodes;
using ModularRuntime.Contracts;

namespace ServiceProviderPlugin;

/// <summary>
/// Provides <c>example.text.transform</c> 1.0 with the operations <c>uppercase</c> and
/// <c>reverse</c>. Requests and responses are JSON objects: <c>{"text": "..."}</c>.
/// </summary>
public sealed class ServiceProviderPlugin : IPlugin
{
    public const string ServiceId = "example.text.transform";

    private IPluginLogger? _logger;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;
        context.Services.Register(
            new PluginServiceDefinition(ServiceId, "Text transformation", new Version(1, 0), ["uppercase", "reverse"])
            {
                Metadata = new Dictionary<string, string> { ["description"] = "Uppercases or reverses text." },
            },
            HandleAsync);

        context.Logger.LogInformation($"ServiceProviderPlugin registered '{ServiceId}' 1.0.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("ServiceProviderPlugin shut down; the runtime removes its service.");
        return Task.CompletedTask;
    }

    private static ValueTask<PluginResponse> HandleAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        var text = request.Payload.GetString("text");
        if (text is null)
        {
            return ValueTask.FromResult(PluginResponse.Failure("invalid_payload", "Expected a JSON object with a string 'text' property."));
        }

        var transformed = request.Operation switch
        {
            "uppercase" => text.ToUpperInvariant(),
            "reverse" => new string(text.Reverse().ToArray()),
            _ => null,
        };

        return ValueTask.FromResult(transformed is null
            ? PluginResponse.Failure("operation_not_supported", $"Unknown operation '{request.Operation}'.")
            : PluginResponse.Success(JsonPayload.FromNode(new JsonObject { ["text"] = transformed })));
    }
}
