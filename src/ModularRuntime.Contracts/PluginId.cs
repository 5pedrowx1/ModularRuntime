namespace ModularRuntime.Contracts;

/// <summary>
/// Stable, human-readable identifier of a plugin installation.
/// </summary>
/// <remarks>
/// IDs use lowercase ASCII letters, digits, and the separators <c>.</c>, <c>_</c>,
/// and <c>-</c>. Separators cannot be leading, trailing, or adjacent.
/// </remarks>
public readonly record struct PluginId
{
    public const int MaxLength = RuntimeName.MaxLength;

    public PluginId(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid plugin ID. Use 1-{MaxLength} lowercase ASCII characters, " +
                "digits, '.', '_' or '-', with no leading, trailing, or repeated separators.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static PluginId Parse(string value) => new(value);

    public static bool TryParse(string? value, out PluginId pluginId)
    {
        if (IsValid(value))
        {
            pluginId = new PluginId(value!);
            return true;
        }

        pluginId = default;
        return false;
    }

    public static bool IsValid(string? value) => RuntimeName.IsValid(value);

    public override string ToString() => Value ?? string.Empty;
}
