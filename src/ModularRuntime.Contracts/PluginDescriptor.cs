namespace ModularRuntime.Contracts;

/// <summary>
/// Immutable metadata that identifies a plugin and its entry point.
/// </summary>
public sealed record PluginDescriptor
{
    public PluginDescriptor(
        PluginId id,
        string name,
        Version version,
        Version apiVersion,
        string entryAssembly,
        string entryType,
        int startOrder = 0)
    {
        if (string.IsNullOrEmpty(id.Value))
        {
            throw new ArgumentException("A plugin descriptor requires a plugin ID.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A plugin descriptor requires a non-empty name.", nameof(name));
        }

        if (name.Length > 200)
        {
            throw new ArgumentException("The plugin name cannot exceed 200 characters.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(apiVersion);

        if (string.IsNullOrWhiteSpace(entryAssembly))
        {
            throw new ArgumentException("A plugin descriptor requires an entry assembly.", nameof(entryAssembly));
        }

        if (string.IsNullOrWhiteSpace(entryType))
        {
            throw new ArgumentException("A plugin descriptor requires an entry type.", nameof(entryType));
        }

        if (entryAssembly.Length > 512 || entryType.Length > 512)
        {
            throw new ArgumentException("Entry assembly and entry type names cannot exceed 512 characters.");
        }

        if (startOrder is < MinStartOrder or > MaxStartOrder)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startOrder),
                $"Start order must be between {MinStartOrder} and {MaxStartOrder}.");
        }

        Id = id;
        Name = name;
        Version = version;
        ApiVersion = apiVersion;
        EntryAssembly = entryAssembly;
        EntryType = entryType;
        StartOrder = startOrder;
    }

    public const int MinStartOrder = -10_000;

    public const int MaxStartOrder = 10_000;

    public PluginId Id { get; }

    public string Name { get; }

    /// <summary>Numeric plugin version. Pre-release labels are not supported.</summary>
    public Version Version { get; }

    /// <summary>Minimum plugin API version required by this plugin.</summary>
    public Version ApiVersion { get; }

    /// <summary>Entry assembly path relative to the plugin installation directory.</summary>
    public string EntryAssembly { get; }

    /// <summary>Fully qualified public type implementing <see cref="IPlugin"/>.</summary>
    public string EntryType { get; }

    /// <summary>
    /// Relative start order used by bulk start (ascending) and bulk stop/disposal (descending).
    /// Plugins with equal order keep their registration order. Defaults to 0.
    /// </summary>
    public int StartOrder { get; }
}
