using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Tests;

internal static class TestDescriptors
{
    public static PluginDescriptor Create(string id) =>
        new(
            PluginId.Parse(id),
            $"Test Plugin {id}",
            new Version(1, 0, 0),
            PluginApiVersion.Supported,
            "TestPlugin.dll",
            "TestPlugin.TestPlugin");
}
