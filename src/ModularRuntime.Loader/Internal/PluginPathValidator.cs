namespace ModularRuntime.Loader.Internal;

internal static class PluginPathValidator
{
    public static bool TryResolveEntryAssembly(
        string pluginDirectory,
        string relativePath,
        out string fullPath,
        out string? error)
    {
        fullPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "The entry assembly path is empty.";
            return false;
        }

        if (relativePath.Length > 512)
        {
            error = "The entry assembly path exceeds 512 characters.";
            return false;
        }

        if (relativePath != relativePath.Trim())
        {
            error = "The entry assembly path has leading or trailing whitespace.";
            return false;
        }

        if (relativePath.Contains('\0') || relativePath.Contains(':'))
        {
            error = "The entry assembly path contains an invalid character.";
            return false;
        }

        if (Path.IsPathRooted(relativePath))
        {
            error = "The entry assembly path must be relative to the plugin directory.";
            return false;
        }

        if (!relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            error = "The entry assembly path must point to a .dll file.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(Path.Combine(pluginDirectory, relativePath));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"The entry assembly path is invalid: {exception.Message}";
            fullPath = string.Empty;
            return false;
        }

        var relativeToRoot = Path.GetRelativePath(pluginDirectory, fullPath);
        var escapesRoot =
            relativeToRoot == ".."
            || relativeToRoot.StartsWith("../", StringComparison.Ordinal)
            || relativeToRoot.StartsWith("..\\", StringComparison.Ordinal)
            || Path.IsPathRooted(relativeToRoot);

        if (escapesRoot)
        {
            error = "The entry assembly path escapes the plugin directory.";
            fullPath = string.Empty;
            return false;
        }

        return true;
    }
}
