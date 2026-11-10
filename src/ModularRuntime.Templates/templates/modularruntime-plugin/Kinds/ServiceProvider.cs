using ModularRuntime.Contracts;
using ModularRuntime.Sdk;

namespace ModularRuntimePlugin;

public sealed record TextRequest(string Text);

public sealed record TextResponse(string Text, int Length);

/// <summary>
/// Registers a service with two asynchronous operations. Consumers find it by its ID and call it
/// with JSON; they never reference this assembly.
/// </summary>
public sealed class ModularRuntimePlugin : IPlugin
{
    public const string ServiceId = "sample.text";

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var operations = new PluginOperations()
            .Add<TextRequest, TextResponse>("upper", (request, _, _) =>
                ValueTask.FromResult(new TextResponse(request.Text.ToUpperInvariant(), request.Text.Length)))
            .Add<TextRequest, TextResponse>("reverse", async (request, call, operationCancellation) =>
            {
                // Handlers receive the caller's cancellation token.
                await Task.Yield();
                operationCancellation.ThrowIfCancellationRequested();
                if (request.Text.Length == 0)
                {
                    // An expected failure: the caller receives this code and message.
                    throw new PluginOperationException("empty_text", "Text must not be empty.");
                }

                context.Logger.LogDebug($"reverse called by {call.Caller}.");
                return new TextResponse(new string([.. request.Text.Reverse()]), request.Text.Length);
            });

        // The registration belongs to this plugin instance and is removed when it stops; callers
        // then get "service_not_found" instead of reaching a stopped plugin.
        context.Services.Register(ServiceId, "Text service", new Version(1, 0), operations);
        context.Logger.LogInformation($"Service '{ServiceId}' registered with operations: {string.Join(", ", operations.Names)}.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
