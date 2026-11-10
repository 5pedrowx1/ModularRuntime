namespace ModularRuntime.TestPlugin.Dependency;

/// <summary>Private dependency used to verify per-plugin dependency resolution.</summary>
public static class DependencyMessage
{
    public static string GetMessage() => "private-dependency-ok";
}
