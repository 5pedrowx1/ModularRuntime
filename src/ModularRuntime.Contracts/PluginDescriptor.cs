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
        int startOrder = 0,
        IReadOnlyList<string>? sharedContracts = null)
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

        if (sharedContracts is not null)
        {
            if (sharedContracts.Count > MaxSharedContracts)
            {
                throw new ArgumentException($"At most {MaxSharedContracts} shared contract assemblies can be declared.", nameof(sharedContracts));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assemblyName in sharedContracts)
            {
                if (!IsValidSharedContractName(assemblyName))
                {
                    throw new ArgumentException($"'{assemblyName}' is not a valid shared contract assembly name.", nameof(sharedContracts));
                }

                if (!seen.Add(assemblyName))
                {
                    throw new ArgumentException($"Shared contract assembly '{assemblyName}' is declared more than once.", nameof(sharedContracts));
                }
            }
        }

        Id = id;
        Name = name;
        Version = version;
        ApiVersion = apiVersion;
        EntryAssembly = entryAssembly;
        EntryType = entryType;
        StartOrder = startOrder;
        SharedContracts = sharedContracts is null || sharedContracts.Count == 0 ? [] : Array.AsReadOnly(sharedContracts.ToArray());
    }

    public const int MinStartOrder = -10_000;

    public const int MaxStartOrder = 10_000;

    public const int MaxSharedContracts = 32;

    public const string ReservedSharedContractName = "ModularRuntime.Contracts";

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

    /// <summary>
    /// Simple names of the contract assemblies this plugin shares with other plugins (manifest
    /// <c>sharedContracts</c>), for typed services and typed streams. Each is a <c>.dll</c> in the
    /// installation directory that the runtime loads into a shared, collectible context instead of
    /// the plugin's private one. Empty when the plugin shares none. Added in plugin API 0.4.
    /// </summary>
    public IReadOnlyList<string> SharedContracts { get; }

    /// <summary>
    /// A shared contract assembly name: a simple assembly name (no path, no extension, no version),
    /// 1-200 characters of letters, digits, '.', '_' or '-', not <see cref="ReservedSharedContractName"/>
    /// (always shared) and not another ModularRuntime host assembly.
    /// </summary>
    public static bool IsValidSharedContractName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name != name.Trim())
        {
            return false;
        }

        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ModularRuntime.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "ModularRuntime", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
