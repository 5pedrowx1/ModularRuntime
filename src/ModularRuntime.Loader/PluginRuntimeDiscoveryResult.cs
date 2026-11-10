using ModularRuntime.Contracts;

namespace ModularRuntime.Loader;

/// <summary>Discovery and registration outcome for a <see cref="PluginRuntime"/>.</summary>
public sealed class PluginRuntimeDiscoveryResult
{
    public PluginRuntimeDiscoveryResult(
        PluginDiscoveryResult discovery,
        IEnumerable<PluginOperationResult> registrationFailures,
        int registeredCount)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(registrationFailures);

        Discovery = discovery;
        RegistrationFailures = Array.AsReadOnly(registrationFailures.ToArray());
        RegisteredCount = registeredCount;
    }

    public PluginDiscoveryResult Discovery { get; }

    public IReadOnlyList<PluginOperationResult> RegistrationFailures { get; }

    public int RegisteredCount { get; }

    public bool HasErrors => Discovery.HasErrors || RegistrationFailures.Count > 0;
}
