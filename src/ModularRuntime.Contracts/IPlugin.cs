namespace ModularRuntime.Contracts;

/// <summary>
/// Contract implemented by every plugin.
/// </summary>
/// <remarks>
/// Plugin types must be public, concrete, and expose a public parameterless constructor.
/// Implementations must honor cooperative cancellation and should not throw for expected
/// cancellation requests.
/// </remarks>
public interface IPlugin
{
    /// <summary>
    /// Initializes the plugin instance.
    /// </summary>
    /// <param name="context">Runtime services scoped to this plugin instance.</param>
    /// <param name="cancellationToken">Token for this initialization operation.</param>
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Releases plugin resources. The runtime invokes this at most once for a successfully
    /// initialized instance, and as a best-effort rollback after a failed initialization.
    /// </summary>
    /// <param name="cancellationToken">Token for this shutdown operation.</param>
    Task ShutdownAsync(CancellationToken cancellationToken);
}
