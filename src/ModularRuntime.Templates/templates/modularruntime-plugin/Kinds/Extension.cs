using ModularRuntime.Contracts;
using ModularRuntime.Sdk;

namespace ModularRuntimePlugin;

public sealed record FormatRequest(string Text);

public sealed record FormatResponse(string Text);

/// <summary>
/// Contributes a formatter to an extension point declared by the host (or by another plugin).
/// The host lists the contributions and invokes them; this plugin never sees the host's types.
/// </summary>
public sealed class ModularRuntimePlugin : IPlugin
{
    public const string ExtensionPointId = "sample.formatters";

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Extensions.GetExtensionPoint(ExtensionPointId) is not { } point)
        {
            // Contributing requires the point to exist; this host does not offer it.
            context.Logger.LogWarning($"Extension point '{ExtensionPointId}' is not available; nothing contributed.");
            return Task.CompletedTask;
        }

        var contributionId = context.PluginId.Value + ".formatter";
        context.Extensions.Contribute(
            new ExtensionContributionDefinition(contributionId, ExtensionPointId, point.ContractVersion) { DisplayName = context.Descriptor.Name },
            new PluginOperations().Add<FormatRequest, FormatResponse>("format", (request, _, _) =>
                ValueTask.FromResult(new FormatResponse($"[{request.Text.ToUpperInvariant()}]"))));
        context.Logger.LogInformation($"Contributed '{contributionId}' to '{ExtensionPointId}' {point.ContractVersion}.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
