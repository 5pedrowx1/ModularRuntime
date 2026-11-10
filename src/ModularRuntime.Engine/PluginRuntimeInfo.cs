using ModularRuntime.Contracts;

namespace ModularRuntime.Engine;

/// <summary>Observable state of a registered plugin.</summary>
public sealed record PluginRuntimeInfo(
    PluginDescriptor Descriptor,
    PluginState State,
    Guid? InstanceId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    PluginErrorInfo? LastError);
