using System.Text.Json.Serialization;

namespace ModularRuntime.Loader.Internal;

internal sealed class PluginManifestDocument
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("apiVersion")]
    public string? ApiVersion { get; init; }

    [JsonPropertyName("entryAssembly")]
    public string? EntryAssembly { get; init; }

    [JsonPropertyName("entryType")]
    public string? EntryType { get; init; }

    /// <summary>Optional relative start order (default 0); see <c>PluginDescriptor.StartOrder</c>.</summary>
    [JsonPropertyName("startOrder")]
    public int? StartOrder { get; init; }

    /// <summary>Optional short description shown by package managers (at most 1000 characters).</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Optional author or publisher name (at most 200 characters).</summary>
    [JsonPropertyName("author")]
    public string? Author { get; init; }
}
