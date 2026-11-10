using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModularRuntime.TestSupport;

/// <summary>
/// Access to plugin outputs copied into the test output directory by the test projects'
/// build targets. Tests copy these assets into isolated temporary workspaces.
/// </summary>
public static class StagedPlugins
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "test-plugins");

    public static string CopyPlugin(
        string destinationPluginRoot,
        string stagedPluginName,
        string? installationDirectoryName = null)
    {
        var source = Path.Combine(Root, stagedPluginName);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                $"Staged plugin '{stagedPluginName}' was not found at '{source}'. " +
                "The test build target did not copy plugin assets.");
        }

        var destination = Path.Combine(
            destinationPluginRoot,
            installationDirectoryName ?? stagedPluginName);
        CopyDirectory(source, destination);
        return destination;
    }

    public static JsonObject ReadManifest(string pluginDirectory)
    {
        var manifestPath = Path.Combine(pluginDirectory, "plugin.json");
        return JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
            ?? throw new InvalidOperationException($"Manifest '{manifestPath}' is not a JSON object.");
    }

    public static void WriteManifest(string pluginDirectory, JsonObject manifest)
    {
        var manifestPath = Path.Combine(pluginDirectory, "plugin.json");
        File.WriteAllText(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void WriteRawManifest(string pluginDirectory, string json) =>
        File.WriteAllText(Path.Combine(pluginDirectory, "plugin.json"), json);

    public static string GetEntryAssemblyPath(string pluginDirectory, string manifestEntryAssembly) =>
        Path.Combine(pluginDirectory, manifestEntryAssembly);

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var sourceFile in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, sourceFile);
            var destinationFile = Path.Combine(destination, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
    }
}
