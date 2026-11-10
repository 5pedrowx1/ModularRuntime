namespace ModularRuntime.TestPlugin;

/// <summary>
/// Exception type defined inside the plugin assembly. Tests use it to prove that
/// retained results and logs do not keep plugin types or load contexts alive.
/// </summary>
public sealed class PluginFailureException : Exception
{
    public PluginFailureException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Plugin-defined exception whose diagnostic members throw. Exercises the runtime's
/// defensive diagnostic capture inside a collectible load context.
/// </summary>
public sealed class HostilePluginException : Exception
{
    public override string Message => throw new InvalidOperationException("Message getter failed.");

    public override string? StackTrace => throw new InvalidOperationException("StackTrace getter failed.");

    public override string ToString() => throw new InvalidOperationException("ToString failed.");
}
