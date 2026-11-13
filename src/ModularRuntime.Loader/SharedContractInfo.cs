using ModularRuntime.Contracts;

namespace ModularRuntime.Loader;

/// <summary>Diagnostic view of one loaded shared contract assembly.</summary>
/// <param name="Name">Assembly simple name.</param>
/// <param name="Version">Version loaded into the shared context.</param>
/// <param name="UserCount">Plugins currently holding it.</param>
/// <param name="Users">Those plugins.</param>
/// <param name="LoadedFrom">The file whose bytes were loaded.</param>
public sealed record SharedContractInfo(string Name, Version Version, int UserCount, IReadOnlyList<PluginId> Users, string LoadedFrom);
