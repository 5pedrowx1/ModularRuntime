using ModularRuntime.Contracts;
using ModularRuntime.Sdk;

namespace ModularRuntimePlugin;

/// <summary>This plugin's own request and response types for the service's JSON contract.</summary>
public sealed record TextRequest(string Text);

public sealed record TextResponse(string Text, int Length);

/// <summary>
/// Calls a service by ID every few seconds. The provider is another plugin that may start later,
/// stop, or be missing: every call returns a result, never a stale reference to the provider.
/// </summary>
public sealed class ModularRuntimePlugin : IPlugin
{
    public const string ServiceId = "sample.text";

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RunInBackground("consumer", async stoppingToken =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            do
            {
                var descriptor = context.Services.GetService(ServiceId);
                var result = await context.Services.InvokeAsync<TextRequest, TextResponse>(
                    ServiceId, "upper", new TextRequest($"hello from {context.PluginId}"), cancellationToken: stoppingToken);
                if (result.IsSuccess)
                {
                    context.Logger.LogInformation($"Service '{ServiceId}' {descriptor?.Version} answered: {result.Value?.Text}");
                }
                else
                {
                    // service_not_found / service_unavailable while the provider is not running.
                    context.Logger.LogWarning($"Service '{ServiceId}' call failed [{result.ErrorCode}]: {result.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        });
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
