using ModularRuntime.Contracts;
using ModularRuntime.Engine;

namespace ModularRuntime.Loader;

/// <summary>Configuration for an independent <see cref="PluginRuntime"/> instance.</summary>
public sealed record PluginRuntimeOptions
{
    /// <summary>Directory whose immediate subdirectories are plugin installations.</summary>
    public required string PluginDirectory { get; init; }

    /// <summary>Runtime and plugin logger. Defaults to a no-op logger.</summary>
    public IRuntimeLogger Logger { get; init; } = NullRuntimeLogger.Instance;

    /// <summary>Payload, drain, and dispatch limits of the extensibility platform.</summary>
    public RuntimeLimits Limits { get; init; } = new();

    /// <summary>
    /// Optional verification run immediately before each plugin's assemblies are loaded (for
    /// example package signature and file-hash checks). <see langword="null"/> loads without it.
    /// </summary>
    public IPluginLoadVerifier? LoadVerifier { get; init; }

    /// <summary>Clock used for unload waits. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
