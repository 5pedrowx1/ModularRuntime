using System.Text.Json.Nodes;
using ModularRuntime.Contracts;

namespace ExtensionContributorPlugin;

/// <summary>
/// Contributes a Markdown exporter to the <c>example.document.exporters</c> extension point
/// declared by the embedding application. Operation <c>export</c> receives
/// <c>{"title": "...", "body": "..."}</c> and returns the exported document.
/// </summary>
public sealed class ExtensionContributorPlugin : IPlugin
{
    public const string ExtensionPointId = "example.document.exporters";

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        try
        {
            context.Extensions.Contribute(
                new ExtensionContributionDefinition("markdown", ExtensionPointId, new Version(1, 0))
                {
                    DisplayName = "Markdown exporter",
                    Metadata = new Dictionary<string, string> { ["fileExtension"] = ".md" },
                },
                ExportAsync);
            context.Logger.LogInformation($"ExtensionContributorPlugin contributed 'markdown' to '{ExtensionPointId}'.");
        }
        catch (PluginRuntimeException exception) when (exception.ErrorCode == PluginErrorCodes.ExtensionPointNotFound)
        {
            // Extension points are declared by hosts or other plugins; this host has none.
            context.Logger.LogInformation(
                $"ExtensionContributorPlugin: extension point '{ExtensionPointId}' is not declared by this host; nothing contributed.");
        }

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static ValueTask<PluginResponse> ExportAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        var title = request.Payload.GetString("title") ?? "Untitled";
        var body = request.Payload.GetString("body") ?? string.Empty;
        var document = new JsonObject
        {
            ["format"] = "markdown",
            ["content"] = $"# {title}\n\n{body}\n",
        };
        return ValueTask.FromResult(PluginResponse.Success(JsonPayload.FromNode(document)));
    }
}
