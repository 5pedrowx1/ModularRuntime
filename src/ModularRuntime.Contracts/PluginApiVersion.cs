namespace ModularRuntime.Contracts;

/// <summary>
/// Plugin API version policy.
/// </summary>
/// <remarks>
/// The API is experimental (0.x) while ModularRuntime is pre-release. For a 0.x API a plugin
/// must declare exactly the supported <c>major.minor</c> (currently <c>0.3</c>; 0.3 added <c>IPluginContext.Descriptor</c>): any 0.x minor
/// may contain breaking changes. From 1.0 onward the rule becomes "same major, minor less than
/// or equal to the supported minor". Patch-level API versions are not used.
/// </remarks>
public static class PluginApiVersion
{
    public static Version Supported { get; } = new(0, 3);

    public static bool IsCompatible(Version apiVersion)
    {
        ArgumentNullException.ThrowIfNull(apiVersion);
        if (apiVersion.Major != Supported.Major)
        {
            return false;
        }

        return Supported.Major == 0
            ? apiVersion.Minor == Supported.Minor
            : apiVersion.Minor <= Supported.Minor;
    }
}
